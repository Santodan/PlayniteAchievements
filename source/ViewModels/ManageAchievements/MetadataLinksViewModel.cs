using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Metadata;
using PlayniteAchievements.Views.Helpers;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Manage Achievements Overview: one row per metadata source the game can use, showing the
    /// linked id and whether it was matched or entered, with an editable box, an on/off switch,
    /// and a window for fixing the achievement pairings from the last refresh. Applying an id pins
    /// it; clearing lets matching run again; switching a source off stops it for this game. Each
    /// change refreshes the game once, so it takes effect at once.
    /// </summary>
    public sealed class MetadataLinksViewModel : ObservableObject
    {
        private readonly Func<MetadataLinkStore> _store;
        private readonly Func<IReadOnlyList<IMetadataProvider>> _providers;
        private readonly Func<Guid, IReadOnlyList<AchievementDetail>> _achievements;
        private readonly Func<string> _gameName;
        private readonly Func<PersistedSettings> _settings;
        private readonly Action _onChanged;

        internal MetadataLinksViewModel(
            Func<MetadataLinkStore> store,
            Func<IReadOnlyList<IMetadataProvider>> providers,
            Func<Guid, IReadOnlyList<AchievementDetail>> achievements,
            Func<string> gameName,
            Func<PersistedSettings> settings,
            Action onChanged)
        {
            _store = store;
            _providers = providers;
            _achievements = achievements;
            _gameName = gameName;
            _settings = settings;
            _onChanged = onChanged;
        }

        /// <summary>
        /// What a source supplies to this game under its platform's Sources settings, as the
        /// Sources grid names the fields ("Icons, Auto Capstone"); empty when nothing.
        /// </summary>
        private static string DescribeSupplies(MetadataChoices choices, IMetadataProvider provider)
        {
            if (choices == null || provider == null)
            {
                return string.Empty;
            }

            bool Uses(string chosen, MetadataField field) =>
                (provider.SupportedFields & field) == field &&
                string.Equals(chosen, provider.Key, StringComparison.OrdinalIgnoreCase);

            var fields = new List<string>();
            if (Uses(choices.Icons, MetadataField.Icons))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_CustomIcons"));
            }

            if (Uses(choices.Categories, MetadataField.Categories))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_Category"));
            }

            if (Uses(choices.Rarity, MetadataField.Rarity))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_Column_Rarity"));
            }

            if (Uses(choices.CapstoneArt, MetadataField.CapstoneArt))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_AutoCapstone"));
            }

            return string.Join(", ", fields);
        }

        public ObservableCollection<MetadataLinkRow> Rows { get; } = new ObservableCollection<MetadataLinkRow>();

        public bool HasRows => Rows.Count > 0;

        internal void Load(Guid gameId, string servicingProviderKey)
        {
            Rows.Clear();
            var store = _store?.Invoke();
            if (gameId != Guid.Empty && store != null)
            {
                var choices = _settings?.Invoke()?.ResolveMetadataChoices(servicingProviderKey);
                foreach (var provider in (_providers?.Invoke() ?? Array.Empty<IMetadataProvider>())
                    .Where(provider => !provider.IsNativeFor(servicingProviderKey)))
                {
                    store.TryGet(gameId, provider.Key, out var record);
                    Rows.Add(new MetadataLinkRow(
                        this,
                        gameId,
                        provider.Key,
                        ResourceProvider.GetString(provider.DisplayNameKey),
                        provider.LinkPlaceholder,
                        DescribeSupplies(choices, provider),
                        record,
                        store.GetCoverage(gameId, provider.Key),
                        record == null || record.IsNoMatch ? null : store.Snapshots?.Get(gameId, provider.Key, record.Id)));
                }
            }

            OnPropertyChanged(nameof(HasRows));
        }

        /// <summary>Adds a row for a link stored elsewhere, after the metadata sources.</summary>
        internal void AddRow(MetadataLinkRow row)
        {
            if (row == null)
            {
                return;
            }

            Rows.Add(row);
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

        /// <summary>Opens the row's Achievement matches window; a save writes every change together and refreshes once.</summary>
        internal void OpenMatches(MetadataLinkRow row)
        {
            var store = _store?.Invoke();
            if (store == null || row.Snapshot == null || !store.TryGet(row.GameId, row.SourceKey, out var record))
            {
                return;
            }

            var targets = (_achievements?.Invoke(row.GameId) ?? Array.Empty<AchievementDetail>())
                .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName) &&
                                      !CustomAchievementProjectionService.IsCustomApiName(achievement.ApiName))
                .ToList();
            var viewModel = new MetadataMatchesViewModel(row.SourceName, targets, row.Snapshot, record.Pairings);
            var title = string.Join(
                " · ",
                new[] { row.SourceName, _gameName?.Invoke() }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (!MetadataMatchesWindow.Show(null, viewModel, title))
            {
                return;
            }

            if (store.SetPairings(row.GameId, row.SourceKey, viewModel.GetChanges()))
            {
                _onChanged?.Invoke();
            }
        }
    }

    public sealed class MetadataLinkRow : ObservableObject
    {
        private readonly MetadataLinksViewModel _owner;
        private readonly string _linkedId;
        private string _input;
        private bool _isEnabled;

        /// <summary>
        /// A row for a link kept outside the metadata link store (the Exophase rarity page): the
        /// same box, Apply and Reset, with no Enabled switch, count or matches.
        /// </summary>
        internal MetadataLinkRow(
            string sourceName,
            string linkedId,
            string placeholder,
            string supplies,
            Action<string> apply,
            Action reset)
        {
            SourceName = sourceName;
            Placeholder = placeholder;
            Supplies = supplies ?? string.Empty;
            _linkedId = (linkedId ?? string.Empty).Trim();
            _input = _linkedId;
            _isEnabled = true;
            HasLink = _linkedId.Length > 0;
            IsUserSet = HasLink;
            StatusText = string.Empty;
            ApplyCommand = new RelayCommand(
                _ => apply?.Invoke(Input),
                _ => !string.IsNullOrWhiteSpace(Input) &&
                     !string.Equals(Input.Trim(), _linkedId, StringComparison.OrdinalIgnoreCase));
            ClearCommand = new RelayCommand(_ => reset?.Invoke(), _ => IsUserSet);
            OpenMatchesCommand = new RelayCommand(_ => { }, _ => false);
        }

        internal MetadataLinkRow(
            MetadataLinksViewModel owner,
            Guid gameId,
            string sourceKey,
            string sourceName,
            string placeholder,
            string supplies,
            MetadataLinkRecord record,
            MetadataCoverage coverage,
            MetadataSourceSnapshot snapshot)
        {
            _owner = owner;
            GameId = gameId;
            SourceKey = sourceKey;
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceKey : sourceName;
            Placeholder = placeholder;
            Supplies = supplies ?? string.Empty;
            HasEnabledSwitch = true;
            Snapshot = snapshot;
            _input = record?.Id ?? string.Empty;
            _isEnabled = record == null || !record.IsNoMatch;
            StatusText = BuildStatus(record, coverage);
            HasLink = record != null && !record.IsNoMatch;
            IsUserSet = HasLink && record.Origin == MetadataLinkOrigin.User;
            _linkedId = record?.Id ?? string.Empty;
            ShowFixMatches = HasMatches &&
                             ((coverage != null && coverage.Matched < coverage.Total) || record?.Pairings?.Count > 0);
            // Apply pins a new id; an empty box no longer stops matching, the Enabled switch does.
            ApplyCommand = new RelayCommand(
                _ => owner.Apply(this),
                _ => IsEnabled &&
                     !string.IsNullOrWhiteSpace(Input) &&
                     !string.Equals(Input.Trim(), _linkedId, StringComparison.OrdinalIgnoreCase));
            ClearCommand = new RelayCommand(_ => owner.Clear(this), _ => IsUserSet);
            OpenMatchesCommand = new RelayCommand(_ => owner.OpenMatches(this), _ => HasMatches);
        }

        public Guid GameId { get; }

        public string SourceKey { get; }

        public string SourceName { get; }

        /// <summary>The shape of the id the box takes, shown while it is empty.</summary>
        public string Placeholder { get; }

        /// <summary>What the source adds to this game ("Icons, Auto Capstone"); empty when nothing.</summary>
        public string Supplies { get; }

        public bool HasSupplies => !string.IsNullOrEmpty(Supplies);

        /// <summary>A metadata source the user can turn off for this game.</summary>
        public bool HasEnabledSwitch { get; }

        /// <summary>Hidden rather than collapsed without a switch, so every row's box lines up.</summary>
        public System.Windows.Visibility EnabledSwitchVisibility =>
            HasEnabledSwitch ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;

        public string StatusText { get; }

        public bool HasStatus => !string.IsNullOrEmpty(StatusText);

        public bool HasLink { get; }

        /// <summary>The link was entered by the user, so Reset can hand it back to matching.</summary>
        public bool IsUserSet { get; }

        /// <summary>The source's list and matches from the last refresh; null before one, or for a capstone-only source.</summary>
        internal MetadataSourceSnapshot Snapshot { get; }

        public bool HasMatches => Snapshot?.Rows.Count > 0;

        /// <summary>Offered when some achievements are unmatched, or the user already set pairings to revisit.</summary>
        public bool ShowFixMatches { get; }

        public string Input
        {
            get => _input;
            set
            {
                SetValue(ref _input, value);
                // Apply waits for a new id, so the button follows the box as it is typed in.
                (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
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
                (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                _owner?.SetEnabled(this, value);
            }
        }

        public ICommand ApplyCommand { get; }

        public ICommand ClearCommand { get; }

        public ICommand OpenMatchesCommand { get; }

        /// <summary>
        /// How much of the game the source matched at the last refresh, "matched/total"; empty
        /// while the source is off or has not run for this game yet.
        /// </summary>
        private static string BuildStatus(MetadataLinkRecord record, MetadataCoverage coverage)
        {
            return record == null || record.IsNoMatch || coverage == null || coverage.Total <= 0
                ? string.Empty
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0}/{1}", coverage.Matched, coverage.Total);
        }
    }
}
