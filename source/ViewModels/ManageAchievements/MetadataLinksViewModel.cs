using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Manage Achievements Overview: one row per metadata source the game can use, showing the
    /// linked id and whether it was matched or entered, with an editable box, an on/off switch,
    /// and the achievement pairings from the last refresh. Applying an id pins it; clearing lets
    /// matching run again; switching a source off stops it for this game. Each change refreshes
    /// the game, so it takes effect at once.
    /// </summary>
    public sealed class MetadataLinksViewModel : ObservableObject
    {
        private readonly Func<MetadataLinkStore> _store;
        private readonly Func<IReadOnlyList<IMetadataProvider>> _providers;
        private readonly Func<Guid, IReadOnlyList<AchievementDetail>> _achievements;
        private readonly Action _onChanged;

        // Survive the reload each change causes, so the list a user is working in stays open.
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _showingAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Guid _gameId;

        internal MetadataLinksViewModel(
            Func<MetadataLinkStore> store,
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Func<Guid, IReadOnlyList<AchievementDetail>> achievements,
            Action onChanged)
        {
            _store = store;
            _providers = providers;
            _achievements = achievements;
            _onChanged = onChanged;
        }

        public ObservableCollection<MetadataLinkRow> Rows { get; } = new ObservableCollection<MetadataLinkRow>();

        public bool HasRows => Rows.Count > 0;

        internal void Load(Guid gameId, string servicingProviderKey)
        {
            if (gameId != _gameId)
            {
                _expanded.Clear();
                _showingAll.Clear();
                _gameId = gameId;
            }

            Rows.Clear();
            var store = _store?.Invoke();
            if (gameId != Guid.Empty && store != null)
            {
                var targets = (_achievements?.Invoke(gameId) ?? Array.Empty<AchievementDetail>())
                    .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName) &&
                                          !CustomAchievementProjectionService.IsCustomApiName(achievement.ApiName))
                    .ToList();
                foreach (var provider in (_providers?.Invoke() ?? Array.Empty<IMetadataProvider>())
                    .Where(provider => !provider.IsNativeFor(servicingProviderKey)))
                {
                    store.TryGet(gameId, provider.Key, out var record);
                    var snapshot = record == null || record.IsNoMatch
                        ? null
                        : store.Snapshots?.Get(gameId, provider.Key, record.Id);
                    Rows.Add(new MetadataLinkRow(
                        this,
                        gameId,
                        provider.Key,
                        ResourceProvider.GetString(provider.DisplayNameKey),
                        record,
                        store.GetCoverage(gameId, provider.Key),
                        snapshot,
                        targets,
                        _expanded.Contains(provider.Key),
                        _showingAll.Contains(provider.Key)));
                }
            }

            OnPropertyChanged(nameof(HasRows));
        }

        internal void Apply(MetadataLinkRow row)
        {
            _store?.Invoke()?.SetUser(row.GameId, row.SourceKey, row.Input);
            _onChanged?.Invoke();
        }

        internal void Clear(MetadataLinkRow row)
        {
            _store?.Invoke()?.Clear(row.GameId, row.SourceKey);
            _onChanged?.Invoke();
        }

        /// <summary>Off stores the user's "no match"; on removes it so matching runs again.</summary>
        internal void SetEnabled(MetadataLinkRow row, bool enabled)
        {
            var store = _store?.Invoke();
            if (enabled)
            {
                store?.Clear(row.GameId, row.SourceKey);
            }
            else
            {
                store?.SetUser(row.GameId, row.SourceKey, null);
            }

            _onChanged?.Invoke();
        }

        internal void SetPairing(MetadataLinkRow row, string targetApiName, string sourceApiName)
        {
            if (_store?.Invoke()?.SetPairing(row.GameId, row.SourceKey, targetApiName, sourceApiName) == true)
            {
                _onChanged?.Invoke();
            }
        }

        internal void Remember(MetadataLinkRow row, bool expanded, bool showingAll)
        {
            Toggle(_expanded, row.SourceKey, expanded);
            Toggle(_showingAll, row.SourceKey, showingAll);
        }

        private static void Toggle(HashSet<string> set, string key, bool on)
        {
            if (on)
            {
                set.Add(key);
            }
            else
            {
                set.Remove(key);
            }
        }
    }

    public sealed class MetadataLinkRow : ObservableObject
    {
        private readonly MetadataLinksViewModel _owner;
        private string _input;
        private bool _isEnabled;
        private bool _isExpanded;
        private bool _showAll;

        internal MetadataLinkRow(
            MetadataLinksViewModel owner,
            Guid gameId,
            string sourceKey,
            string sourceName,
            MetadataLinkRecord record,
            MetadataCoverage coverage,
            MetadataSourceSnapshot snapshot,
            IReadOnlyList<AchievementDetail> targets,
            bool isExpanded,
            bool showAll)
        {
            _owner = owner;
            GameId = gameId;
            SourceKey = sourceKey;
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceKey : sourceName;
            _input = record?.Id ?? string.Empty;
            _isEnabled = record == null || !record.IsNoMatch;
            _isExpanded = isExpanded;
            _showAll = showAll;
            StatusText = BuildStatus(record, coverage);
            HasLink = record != null && !record.IsNoMatch;
            ApplyCommand = new RelayCommand(_ => owner.Apply(this));
            ClearCommand = new RelayCommand(_ => owner.Clear(this), _ => HasLink);
            Matches = BuildMatches(record, snapshot, targets);
        }

        public Guid GameId { get; }

        public string SourceKey { get; }

        public string SourceName { get; }

        public string StatusText { get; }

        public bool HasLink { get; }

        public string Input
        {
            get => _input;
            set => SetValue(ref _input, value);
        }

        /// <summary>Whether the source is used for this game; off is the user's "no match".</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                OnPropertyChanged();
                _owner.SetEnabled(this, value);
            }
        }

        public ICommand ApplyCommand { get; }

        public ICommand ClearCommand { get; }

        /// <summary>One line per achievement, from the source's list at the last refresh; empty before one.</summary>
        public IReadOnlyList<MetadataMatchRow> Matches { get; }

        public bool HasMatches => Matches.Count > 0;

        /// <summary>Unmatched achievements first; all of them when <see cref="ShowAll"/> is on.</summary>
        public IEnumerable<MetadataMatchRow> VisibleMatches => _showAll
            ? Matches
            : Matches.Where(match => !match.IsMatched || match.IsUserSet);

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                OnPropertyChanged();
                _owner.Remember(this, _isExpanded, _showAll);
            }
        }

        public bool ShowAll
        {
            get => _showAll;
            set
            {
                if (_showAll == value)
                {
                    return;
                }

                _showAll = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibleMatches));
                _owner.Remember(this, _isExpanded, _showAll);
            }
        }

        internal void SetPairing(MetadataMatchRow match, string sourceApiName) =>
            _owner.SetPairing(this, match.TargetApiName, sourceApiName);

        private IReadOnlyList<MetadataMatchRow> BuildMatches(
            MetadataLinkRecord record,
            MetadataSourceSnapshot snapshot,
            IReadOnlyList<AchievementDetail> targets)
        {
            if (snapshot == null || snapshot.Rows.Count == 0 || targets == null || targets.Count == 0)
            {
                return Array.Empty<MetadataMatchRow>();
            }

            var options = new List<MetadataMatchOption>
            {
                new MetadataMatchOption(string.Empty, ResourceProvider.GetString("LOCNone"), null, null)
            };
            options.AddRange(snapshot.Rows.Select(row => new MetadataMatchOption(row.ApiName, row.Name, row.Description, row.Icon)));

            var pairings = record?.Pairings ?? new Dictionary<string, string>();
            return targets
                .Select(target =>
                {
                    var isUserSet = pairings.TryGetValue(target.ApiName, out var paired);
                    snapshot.Matches.TryGetValue(target.ApiName, out var matched);
                    return new MetadataMatchRow(
                        this,
                        target.ApiName,
                        target.DisplayName,
                        target.Description,
                        options,
                        isUserSet ? paired ?? string.Empty : matched,
                        isUserSet,
                        !string.IsNullOrWhiteSpace(matched));
                })
                .OrderBy(match => match.IsMatched ? 1 : 0)
                .ToList();
        }

        private static string BuildStatus(MetadataLinkRecord record, MetadataCoverage coverage)
        {
            if (record == null || record.IsNoMatch)
            {
                return ResourceProvider.GetString("LOCPlayAch_Common_Status_NotLinked");
            }

            var origin = record.Origin == MetadataLinkOrigin.User
                ? ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Overview_MetadataLink_SetByYou")
                : ResourceProvider.GetString("LOCAutomatic");
            var parts = new List<string>
            {
                string.IsNullOrWhiteSpace(record.Label) ? record.Id : $"{record.Label} ({record.Id})",
                origin
            };

            // Absent until the next refresh runs the source for this game.
            if (coverage != null && coverage.Total > 0)
            {
                parts.Add(string.Format(
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Overview_MetadataLink_Matched"),
                    coverage.Matched,
                    coverage.Total));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>One of the game's achievements and the source row it is paired with.</summary>
    public sealed class MetadataMatchRow : ObservableObject
    {
        private readonly MetadataLinkRow _owner;
        private string _selectedKey;

        internal MetadataMatchRow(
            MetadataLinkRow owner,
            string targetApiName,
            string targetName,
            string targetDescription,
            IReadOnlyList<MetadataMatchOption> options,
            string selectedKey,
            bool isUserSet,
            bool isMatched)
        {
            _owner = owner;
            TargetApiName = targetApiName;
            TargetName = string.IsNullOrWhiteSpace(targetName) ? targetApiName : targetName;
            TargetDescription = targetDescription;
            Options = options;
            _selectedKey = selectedKey;
            IsUserSet = isUserSet;
            IsMatched = isMatched;
            ResetCommand = new RelayCommand(_ => _owner.SetPairing(this, null), _ => IsUserSet);
        }

        public string TargetApiName { get; }

        public string TargetName { get; }

        public string TargetDescription { get; }

        public IReadOnlyList<MetadataMatchOption> Options { get; }

        /// <summary>A source row's ApiName, an empty string for "none", or null while unmatched.</summary>
        public string SelectedKey
        {
            get => _selectedKey;
            set
            {
                if (string.Equals(_selectedKey, value, StringComparison.Ordinal) || value == null)
                {
                    return;
                }

                _selectedKey = value;
                OnPropertyChanged();
                _owner.SetPairing(this, value);
            }
        }

        public bool IsUserSet { get; }

        /// <summary>Matched at the last refresh, by the user's pairing or by name.</summary>
        public bool IsMatched { get; }

        public bool IsUnmatched => string.IsNullOrWhiteSpace(_selectedKey);

        public ICommand ResetCommand { get; }
    }

    /// <summary>A source row offered in a pairing dropdown; an empty key is "none".</summary>
    public sealed class MetadataMatchOption
    {
        public MetadataMatchOption(string key, string name, string description, string icon)
        {
            Key = key;
            Name = name;
            Description = description;
            Icon = icon;
        }

        public string Key { get; }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }
    }
}
