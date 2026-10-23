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
using PlayniteAchievements.Views.Settings.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Manage Achievements Overview: the game's own source choices (one row of the Metadata tab's
    /// grid), then one row per metadata source the game can use, showing the linked id with an
    /// editable box and a window for fixing the achievement pairings from the last refresh.
    /// Applying an id pins it; Reset lets matching run again; choosing "Own" for a source's fields
    /// stops it for this game. Each change refreshes the game once, so it takes effect at once.
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
        /// What a source supplies to this game, as the Sources grid names the fields ("Icons,
        /// Auto Capstone"); empty when nothing. <paramref name="choices"/> are the routed ones, so
        /// a field one source serves for another's choice names that choice ("Points (Xbox)").
        /// </summary>
        private static string DescribeSupplies(
            MetadataChoices choices,
            MetadataChoices chosen,
            IMetadataProvider provider,
            IReadOnlyList<IMetadataProvider> providers)
        {
            if (choices == null || provider == null)
            {
                return string.Empty;
            }

            bool Uses(string routed, MetadataField field) =>
                (provider.SupportedFields & field) == field &&
                string.Equals(routed, provider.Key, StringComparison.OrdinalIgnoreCase);

            string Served(string label, string picked)
            {
                if (string.IsNullOrWhiteSpace(picked) || string.Equals(picked, provider.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return label;
                }

                var source = providers?.FirstOrDefault(candidate => string.Equals(candidate?.Key, picked, StringComparison.OrdinalIgnoreCase));
                return source == null ? label : $"{label} ({ResourceProvider.GetString(source.DisplayNameKey)})";
            }

            var fields = new List<string>();
            if (Uses(choices.Icons, MetadataField.Icons))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_CustomIcons"));
            }

            if (Uses(choices.Categories, MetadataField.Categories))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_Category"));
            }

            if (Uses(choices.Tags, MetadataField.Tags))
            {
                fields.Add(ResourceProvider.GetString("LOCTagsLabel"));
            }

            if (Uses(choices.Rarity, MetadataField.Rarity))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_Column_Rarity"));
            }

            if (Uses(choices.CapstoneArt, MetadataField.CapstoneArt))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_AutoCapstone"));
            }

            if (Uses(choices.TrophyTypes, MetadataField.TrophyTypes))
            {
                fields.Add(Served(ResourceProvider.GetString("LOCPlayAch_Column_Trophy"), chosen?.TrophyTypes));
            }

            if (Uses(choices.Points, MetadataField.Points))
            {
                fields.Add(Served(ResourceProvider.GetString("LOCPlayAch_Column_Points"), chosen?.Points));
            }

            return string.Join(", ", fields);
        }

        public ObservableCollection<MetadataLinkRow> Rows { get; } = new ObservableCollection<MetadataLinkRow>();

        public bool HasRows => Rows.Count > 0;

        /// <summary>True for a game with a choices row, which shows even before any source is in use.</summary>
        public bool HasChoices => ChoiceRows.Count > 0;

        /// <summary>
        /// The game's own source choices, one row of the Metadata tab's grid: "Default" follows the
        /// game's platform, "Own" turns a source off for this game.
        /// </summary>
        public IReadOnlyList<MetadataChoiceRow> ChoiceRows { get; private set; } = Array.Empty<MetadataChoiceRow>();

        private Guid _choicesGameId;
        private string _choicesProviderKey;

        internal void Load(Guid gameId, string servicingProviderKey)
        {
            Rows.Clear();
            var store = _store?.Invoke();
            // A platform no source has gets no metadata section.
            if (gameId == Guid.Empty || store == null || !MetadataChoices.TakesSources(servicingProviderKey))
            {
                SetChoiceRows(Guid.Empty, null, Array.Empty<MetadataChoiceRow>());
            }
            else
            {
                var gameChoices = store.GetChoices(gameId);
                // The same row for the same game, so a reload never replaces a dropdown mid-selection.
                if (_choicesGameId != gameId || !string.Equals(_choicesProviderKey, servicingProviderKey, StringComparison.OrdinalIgnoreCase))
                {
                    SetChoiceRows(gameId, servicingProviderKey, new[]
                    {
                        new MetadataChoiceRow(
                            _providers?.Invoke() ?? Array.Empty<IMetadataProvider>(),
                            servicingProviderKey,
                            gameChoices,
                            // "Default" stands for the platform's choices, without the game's own.
                            () => _settings?.Invoke()?.ResolveMetadataChoices(servicingProviderKey),
                            changed => SaveChoices(gameId, servicingProviderKey, changed))
                    });
                }

                // A source's link shows while the game uses the source, or while it holds a link the
                // user set, so a hand-entered link never disappears with a choice.
                // Rows follow where the data comes from: points and ranks show on the source that
                // serves them, named after the choice they serve.
                var providers = _providers?.Invoke() ?? Array.Empty<IMetadataProvider>();
                var chosen = _settings?.Invoke()?.ResolveMetadataChoices(servicingProviderKey, gameChoices);
                var choices = MetadataSourceRouting.Route(chosen, providers.ToList(), servicingProviderKey);
                foreach (var provider in providers.Where(provider => !provider.IsNativeFor(servicingProviderKey)))
                {
                    store.TryGet(gameId, provider.Key, out var record);
                    var supplies = DescribeSupplies(choices, chosen, provider, providers);
                    if (string.IsNullOrEmpty(supplies) && record?.Origin != MetadataLinkOrigin.User)
                    {
                        continue;
                    }

                    // A source the game's refresh has not reached yet has nothing to show: no
                    // link, no stored no-match and no match count. It appears after a refresh.
                    var coverage = store.GetCoverage(gameId, provider.Key);
                    if (record == null && coverage == null)
                    {
                        continue;
                    }

                    Rows.Add(new MetadataLinkRow(
                        this,
                        gameId,
                        provider.Key,
                        ResourceProvider.GetString(provider.DisplayNameKey),
                        provider.LinkPlaceholder,
                        supplies,
                        record,
                        coverage,
                        record == null || record.IsNoMatch ? null : store.Snapshots?.Get(gameId, provider.Key, record.Id),
                        record?.Id == null ? null : MetadataLinkIds.ShownLink(provider as IMetadataLinkDetails, record.Id))
                    {
                        LinkDetails = record == null || record.IsNoMatch ? null : MetadataLinkIds.DescribeLink(provider as IMetadataLinkDetails, gameId, record.Id),
                        PageLinks = record == null || record.IsNoMatch
                            ? Array.Empty<MetadataPageLinkItem>()
                            : MetadataLinkIds.PageLinks(provider as IMetadataPageLinks, gameId, record.Id, servicingProviderKey)
                                .Select(page => new MetadataPageLinkItem(page.Label, page.Url))
                                .ToList()
                    });
                }
            }

            OnPropertyChanged(nameof(HasRows));
        }

        private void SetChoiceRows(Guid gameId, string servicingProviderKey, IReadOnlyList<MetadataChoiceRow> rows)
        {
            _choicesGameId = gameId;
            _choicesProviderKey = servicingProviderKey;
            ChoiceRows = rows;
            OnPropertyChanged(nameof(ChoiceRows));
            OnPropertyChanged(nameof(HasChoices));
        }

        /// <summary>
        /// Saves the game's choices and updates what each source supplies. No refresh: several
        /// choices can be changed, then the game's Refresh applies them together.
        /// </summary>
        private void SaveChoices(Guid gameId, string servicingProviderKey, MetadataChoices changed)
        {
            var store = _store?.Invoke();
            if (store == null || !store.SetChoices(gameId, changed))
            {
                return;
            }

            // The sources in use changed, so the link rows do; the choices row is kept.
            Load(gameId, servicingProviderKey);
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

    /// <summary>One page a metadata link uses, opened in the browser.</summary>
    public sealed class MetadataPageLinkItem
    {
        internal MetadataPageLinkItem(string label, string url)
        {
            Label = string.IsNullOrWhiteSpace(label) ? url : label;
            Url = url;
            OpenCommand = new RelayCommand(_ => Open());
        }

        public string Label { get; }

        public string Url { get; }

        public ICommand OpenCommand { get; }

        private void Open()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Debug($"[Metadata] Opening {Url} failed: {ex.Message}");
            }
        }
    }

    public sealed class MetadataLinkRow : ObservableObject
    {
        private readonly string _linkedId;
        private string _input;
        private string _supplies = string.Empty;

        /// <summary>
        /// A row for a link kept outside the metadata link store (the Exophase rarity page): the
        /// same box, Apply and Reset, with no count or matches.
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
            MetadataSourceSnapshot snapshot,
            string shownLinkId = null)
        {
            GameId = gameId;
            SourceKey = sourceKey;
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceKey : sourceName;
            // A link can name several lists (a collection's games), separated by commas.
            Placeholder = string.IsNullOrWhiteSpace(placeholder) ? placeholder : placeholder + MetadataLinkIds.Separator + " …";
            Supplies = supplies ?? string.Empty;
            Snapshot = snapshot;
            StatusToolTip = BuildListStatus(snapshot);
            // The box shows the link as the source names it, so a stored form the user never typed
            // does not show, and Apply stays off until the shown id changes.
            _input = shownLinkId ?? record?.Id ?? string.Empty;
            StatusText = BuildStatus(record, coverage);
            HasLink = record != null && !record.IsNoMatch;
            // A stored "no match" (the retired Enabled switch's off) is the user's too, so Reset
            // hands it back to matching.
            IsUserSet = record?.Origin == MetadataLinkOrigin.User;
            _linkedId = _input;
            ShowFixMatches = HasMatches;
            // Apply pins a new id; turning a source off is the game's "Own" choice for its fields.
            ApplyCommand = new RelayCommand(
                _ => owner.Apply(this),
                _ => !string.IsNullOrWhiteSpace(Input) &&
                     !string.Equals(Input.Trim(), _linkedId, StringComparison.OrdinalIgnoreCase));
            CanReset = IsUserSet || record?.IsNoMatch == true;
            ClearCommand = new RelayCommand(_ => owner.Clear(this), _ => CanReset);
            OpenMatchesCommand = new RelayCommand(_ => owner.OpenMatches(this), _ => HasMatches);
        }

        public Guid GameId { get; }

        public string SourceKey { get; }

        public string SourceName { get; }

        /// <summary>The shape of the id the box takes, shown while it is empty.</summary>
        public string Placeholder { get; }

        /// <summary>What the source adds to this game ("Icons, Auto Capstone"); empty when nothing.</summary>
        public string Supplies
        {
            get => _supplies;
            internal set
            {
                SetValue(ref _supplies, value ?? string.Empty);
                OnPropertyChanged(nameof(HasSupplies));
            }
        }

        public bool HasSupplies => !string.IsNullOrEmpty(Supplies);

        /// <summary>Where the link led for this game, page by page; null for a source with one page.</summary>
        public string LinkDetails { get; internal set; }

        /// <summary>The pages the link uses; the row's one button opens the page, or a menu of them when there are several.</summary>
        public IReadOnlyList<MetadataPageLinkItem> PageLinks { get; internal set; } = Array.Empty<MetadataPageLinkItem>();

        public bool HasPageLinks => PageLinks.Count > 0;

        /// <summary>The button's tooltip: the one page's address, or each page's name and address.</summary>
        public string PageLinksToolTip => PageLinks.Count == 1
            ? PageLinks[0].Url
            : string.Join(Environment.NewLine, PageLinks.Select(page => page.Label + ": " + page.Url));

        public string StatusText { get; }

        public bool HasStatus => !string.IsNullOrEmpty(StatusText);

        /// <summary>For a link that names several lists, each list's matched rows ("Mass Effect 2: 44/45"); null otherwise.</summary>
        public string StatusToolTip { get; }

        private static string BuildListStatus(MetadataSourceSnapshot snapshot)
        {
            var rows = snapshot?.Rows ?? new List<MetadataSourceRow>();
            if (!rows.Any(row => !string.IsNullOrEmpty(row.List)))
            {
                return null;
            }

            var matched = new HashSet<string>(snapshot.Matches.Values, StringComparer.OrdinalIgnoreCase);
            return string.Join(Environment.NewLine, rows
                .GroupBy(row => row.List ?? string.Empty)
                .Select(list => $"{list.Key}: {list.Count(row => matched.Contains(row.ApiName))}/{list.Count()}"));
        }

        public bool HasLink { get; }

        /// <summary>The link was entered by the user, so Reset can hand it back to matching.</summary>
        public bool IsUserSet { get; }

        /// <summary>
        /// Reset hands the link back to matching: one the user entered, or a "no match" the
        /// source's matching stored, so it looks again on the next refresh.
        /// </summary>
        public bool CanReset { get; }

        /// <summary>The source's list and matches from the last refresh; null before one, or for a capstone-only source.</summary>
        internal MetadataSourceSnapshot Snapshot { get; }

        public bool HasMatches => Snapshot?.Rows.Count > 0;

        /// <summary>Offered whenever the source has a match list from the last refresh, to change any pairing.</summary>
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
