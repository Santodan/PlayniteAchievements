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
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.Services.Metadata;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Metadata;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Manage Achievements Overview: the game's own source choices (one row of the Metadata tab's
    /// grid), then one row per metadata source the game can use, showing the linked game's title,
    /// a search to pick another and a window for fixing the achievement pairings from the last
    /// refresh. Picking a game pins it; Reset lets matching run again; choosing "Own" for a
    /// source's fields stops it for this game. Each change refreshes the game once, so it takes
    /// effect at once.
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
        /// What a source supplies to this game as its own choice, as the Sources grid names the
        /// fields ("Icons, Auto Capstone"); empty when nothing. <paramref name="choices"/> are the
        /// routed ones; a field the source serves for another choice (Xbox points) is listed on that
        /// choice's row instead, not here.
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

            // Null for a field picked as another source that this one serves; that choice's row lists it.
            string Served(string label, string picked)
            {
                if (string.IsNullOrWhiteSpace(picked) || string.Equals(picked, provider.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return label;
                }

                var source = providers?.FirstOrDefault(candidate => string.Equals(candidate?.Key, picked, StringComparison.OrdinalIgnoreCase));
                return source == null ? label : null;
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

            if (Uses(choices.Text, MetadataField.Text))
            {
                fields.Add(ResourceProvider.GetString("LOCPlayAch_Tagging_Area_Text"));
            }

            return string.Join(", ", fields.Where(field => field != null));
        }

        /// <summary>
        /// True when the source serves a field picked as another choice (Xbox points read from
        /// TrueAchievements), so its row shows to hold the link that choice's row points to.
        /// </summary>
        private static bool ServesOtherChoice(MetadataChoices routed, MetadataChoices chosen, IMetadataProvider provider) =>
            routed != null && chosen != null &&
            RoutableFields.Any(entry =>
                string.Equals(entry.Get(routed)?.Trim(), provider.Key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.Get(chosen)) &&
                !string.Equals(entry.Get(chosen).Trim(), provider.Key, StringComparison.OrdinalIgnoreCase));

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
                    var stored = record != null;
                    // A link the game implies (a Steam game's own appId on SteamHunters) is shown in
                    // place of any automatic record; only a link the user set replaces it.
                    var implicitId = record?.Origin == MetadataLinkOrigin.User
                        ? null
                        : (provider as IMetadataImplicitLink)?.ImplicitLinkId(gameId);
                    if (implicitId != null)
                    {
                        // The game's own entry on the source, so it goes by the game's name.
                        record = new MetadataLinkRecord
                        {
                            Id = implicitId,
                            Label = MetadataFoundNames.Format(new[] { ((string)null, _gameName?.Invoke()) }),
                            Origin = MetadataLinkOrigin.Auto
                        };
                    }

                    var supplies = DescribeSupplies(choices, chosen, provider, providers);
                    if (string.IsNullOrEmpty(supplies) &&
                        record?.Origin != MetadataLinkOrigin.User &&
                        !ServesOtherChoice(choices, chosen, provider))
                    {
                        AddRoutedRow(chosen, choices, provider, providers, store, gameId, servicingProviderKey);
                        continue;
                    }

                    // A source the game's refresh has not reached yet has nothing to show: no
                    // link, no stored no-match and no match count. It appears after a refresh.
                    var coverage = store.GetCoverage(gameId, provider.Key);
                    if (record == null && coverage == null)
                    {
                        AddRoutedRow(chosen, choices, provider, providers, store, gameId, servicingProviderKey);
                        continue;
                    }

                    var snapshot = record == null || record.IsNoMatch ? null : store.Snapshots?.Get(gameId, provider.Key, record.Id);
                    var titles = LinkTitles(record, snapshot);
                    var linkedGames = LinkedGames(record, titles, provider as IMetadataLinkDetails);
                    Rows.Add(new MetadataLinkRow(
                        this,
                        gameId,
                        servicingProviderKey,
                        provider.Key,
                        ResourceProvider.GetString(provider.DisplayNameKey),
                        supplies,
                        record,
                        coverage,
                        snapshot,
                        linkedGames,
                        ShownTitle(gameId, record, titles, linkedGames, provider as IMetadataLinkPlatform),
                        provider is IMetadataLinkSearch,
                        stored)
                    {
                        LinkDetails = record == null || record.IsNoMatch ? null : MetadataLinkIds.DescribeLink(provider as IMetadataLinkDetails, gameId, record.Id),
                        LinkIdsText = record == null || record.IsNoMatch
                            ? null
                            : string.Join(Environment.NewLine, MetadataLinkIds.Parse(record.Id)
                                .Select(id => (provider as IMetadataLinkDetails)?.ShownLink(id) ?? id)),
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

        /// <summary>
        /// A row for a choice whose fields another source serves (Xbox points read from
        /// TrueAchievements), so every choice in the grid has a row: it names the source that
        /// serves it. When that source reads the fields from one page, the row shows the page and
        /// can pick another for this choice alone; the source's own row and link stay as they are.
        /// </summary>
        private void AddRoutedRow(
            MetadataChoices chosen,
            MetadataChoices routed,
            IMetadataProvider provider,
            IReadOnlyList<IMetadataProvider> providers,
            MetadataLinkStore store,
            Guid gameId,
            string servicingProviderKey)
        {
            if (chosen == null || routed == null)
            {
                return;
            }

            var away = RoutableFields
                .Where(entry => string.Equals(entry.Get(chosen)?.Trim(), provider.Key, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (entry.LabelKey, ServedBy: entry.Get(routed)?.Trim()))
                .Where(entry => !string.IsNullOrEmpty(entry.ServedBy) &&
                                !string.Equals(entry.ServedBy, provider.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var servedBy = away.Count == 0
                ? null
                : providers.FirstOrDefault(candidate => string.Equals(candidate?.Key, away[0].ServedBy, StringComparison.OrdinalIgnoreCase));
            if (servedBy == null)
            {
                return;
            }

            var sourceName = ResourceProvider.GetString(provider.DisplayNameKey);
            var servedByText = string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_ProviderServicedByFormat"), ResourceProvider.GetString(servedBy.DisplayNameKey));
            var fields = string.Join(", ", away.Select(entry => ResourceProvider.GetString(entry.LabelKey)));
            var pages = servedBy as IMetadataServedPages;
            var pageKey = pages?.ServedPageKey(provider.Key);
            if (pageKey == null || store == null || gameId == Guid.Empty)
            {
                Rows.Add(MetadataLinkRow.Routed(sourceName, servedByText, fields));
                return;
            }

            var picked = store.TryGet(gameId, pageKey, out var pickedRecord) && !pickedRecord.IsNoMatch ? pickedRecord : null;
            var sourceLink = store.TryGet(gameId, servedBy.Key, out var sourceRecord) && !sourceRecord.IsNoMatch ? sourceRecord : null;
            var page = pages.ServedPage(gameId, provider.Key, picked?.Id, sourceLink?.Id);
            // The page goes by the title it was picked under, else its name: the source's own link
            // may have found the game under another edition's title.
            var title = (picked == null ? null : LinkTitles(picked, null).FirstOrDefault()) ?? page?.Title;
            var linkedGames = page == null
                ? Array.Empty<GameSearchResult>()
                : new[] { new GameSearchResult(page.Slug, title, platforms: page.Platform) };
            Rows.Add(MetadataLinkRow.ServedPage(
                this,
                gameId,
                servicingProviderKey,
                pageKey,
                provider.Key,
                servedBy.Key,
                sourceName,
                page == null
                    ? servedByText
                    : string.IsNullOrWhiteSpace(page.Platform)
                        ? title
                        : string.Format(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Manual_LinkSummary"), title, page.Platform),
                // Still says which source reads the page, now that the title shows the page.
                page == null ? fields : string.Join(" · ", fields, servedByText),
                servedByText,
                picked != null,
                linkedGames,
                page == null ? null : new MetadataPageLinkItem(page.Slug, page.Url)));
        }

        /// <summary>
        /// Opens the search for the page a routed choice is read from; the page picked is kept for
        /// that choice alone, under the title it was picked by.
        /// </summary>
        internal void ChangeServedPage(MetadataLinkRow row)
        {
            var store = _store?.Invoke();
            var pages = (_providers?.Invoke() ?? Array.Empty<IMetadataProvider>())
                .FirstOrDefault(candidate => string.Equals(candidate?.Key, row.ServedByKey, StringComparison.OrdinalIgnoreCase)) as IMetadataServedPages;
            var search = pages?.CreateServedPageSearch(row.ServedChoiceKey, row.SourceName);
            if (store == null || search == null)
            {
                return;
            }

            var gameName = _gameName?.Invoke();
            var title = string.Join(
                " · ",
                new[] { row.SourceName, gameName }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            var picked = GameSearchWindow.Show(null, search, title, gameName, row.LinkedGames);
            var result = picked?.FirstOrDefault();
            if (result == null ||
                (row.IsUserSet && string.Equals(result.Id, row.LinkedGames.FirstOrDefault()?.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var name = string.Equals(result.Name, result.Id, StringComparison.OrdinalIgnoreCase) ? null : result.Name;
            store.SetUser(row.GameId, row.SourceKey, result.Id, MetadataFoundNames.Format(new[] { ((string)null, name) }));
            _onChanged?.Invoke();
        }

        /// <summary>The fields routing can move to another source: points and trophy ranks.</summary>
        private static readonly (Func<MetadataChoices, string> Get, string LabelKey)[] RoutableFields =
        {
            (choices => choices.TrophyTypes, "LOCPlayAch_Column_Trophy"),
            (choices => choices.Points, "LOCPlayAch_Column_Points")
        };

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

        /// <summary>
        /// The titles the source found a link's games by: the link's label, else the list titles
        /// from the last refresh. Empty when neither has any.
        /// </summary>
        internal static IReadOnlyList<string> LinkTitles(MetadataLinkRecord record, MetadataSourceSnapshot snapshot)
        {
            if (record == null || record.IsNoMatch)
            {
                return Array.Empty<string>();
            }

            var titles = MetadataFoundNames.Parse(record.Label)
                .Select(name => name.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return titles.Count > 0
                ? titles
                : (snapshot?.Rows ?? new List<MetadataSourceRow>())
                    .Select(row => row.List)
                    .Where(list => !string.IsNullOrWhiteSpace(list))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        /// <summary>
        /// The link's titles as its row shows them, each followed by the console of the list it
        /// leads to when the source says ("BioShock (PS4)"). Display only: the label keeps the
        /// bare titles, which other sources search by.
        /// </summary>
        internal static string ShownTitle(
            Guid gameId,
            MetadataLinkRecord record,
            IReadOnlyList<string> titles,
            IReadOnlyList<GameSearchResult> linkedGames,
            IMetadataLinkPlatform platforms)
        {
            titles = titles ?? Array.Empty<string>();
            linkedGames = linkedGames ?? Array.Empty<GameSearchResult>();
            var ids = record == null || record.IsNoMatch ? new List<string>() : MetadataLinkIds.Parse(record.Id).ToList();

            string WithPlatform(string title, string id)
            {
                var platform = platforms == null || id == null ? null : platforms.ShownPlatform(gameId, id);
                return string.IsNullOrWhiteSpace(platform)
                    ? title
                    : string.Format(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Manual_LinkSummary"), title, platform);
            }

            if (titles.Count == 0)
            {
                return string.Join(", ", linkedGames.Select((game, index) => WithPlatform(game.Name, index < ids.Count ? ids[index] : null)));
            }

            if (titles.Count == ids.Count)
            {
                return string.Join(", ", titles.Select((title, index) => WithPlatform(title, ids[index])));
            }

            var joined = string.Join(", ", titles);
            return ids.Count == 1 ? WithPlatform(joined, ids[0]) : joined;
        }

        /// <summary>
        /// The games a link names, in its order, each under its title when there is one title per
        /// game and under the link as the source shows it otherwise. Empty for no link or a stored
        /// "no match".
        /// </summary>
        internal static IReadOnlyList<GameSearchResult> LinkedGames(
            MetadataLinkRecord record,
            IReadOnlyList<string> titles,
            IMetadataLinkDetails details)
        {
            if (record == null || record.IsNoMatch)
            {
                return Array.Empty<GameSearchResult>();
            }

            var ids = MetadataLinkIds.Parse(record.Id);
            return ids
                .Select((id, index) => new GameSearchResult(
                    id,
                    titles?.Count == ids.Count ? titles[index] : details?.ShownLink(id) ?? id))
                .ToList();
        }

        /// <summary>
        /// Opens the source's game search; the games picked become the link, pinned as the user's,
        /// under the titles they were picked by.
        /// </summary>
        internal void Change(MetadataLinkRow row)
        {
            var store = _store?.Invoke();
            var provider = (_providers?.Invoke() ?? Array.Empty<IMetadataProvider>())
                .FirstOrDefault(candidate => string.Equals(candidate?.Key, row.SourceKey, StringComparison.OrdinalIgnoreCase));
            var search = (provider as IMetadataLinkSearch)?.CreateLinkSearch(row.GameId, row.ServicingProviderKey);
            if (store == null || search == null)
            {
                return;
            }

            var gameName = _gameName?.Invoke();
            var title = string.Join(
                " · ",
                new[] { row.SourceName, gameName }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            var picked = GameSearchWindow.Show(null, search, title, gameName, row.LinkedGames);
            if (picked == null || picked.Count == 0)
            {
                return;
            }

            var id = MetadataLinkIds.Join(picked.Select(result => result.Id));
            if (row.IsUserSet && MetadataLinkIds.SameIds(id, MetadataLinkIds.Join(row.LinkedGames.Select(game => game.Id))))
            {
                return;
            }

            // A game picked by a typed id has only the id for a name, which is not stored as one.
            string Named(GameSearchResult result) =>
                string.Equals(result.Name, result.Id, StringComparison.OrdinalIgnoreCase) ? null : result.Name;

            // Several games are each filed under their own title, so another source never searches
            // one collection game's name for the whole game.
            var label = MetadataFoundNames.Format(picked.Count == 1
                ? new[] { ((string)null, Named(picked[0])) }
                : picked.Select(result => (Named(result), Named(result))));
            store.SetUser(row.GameId, row.SourceKey, id, label);
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
        private string _supplies = string.Empty;

        private MetadataLinkRow(string sourceName, string linkTitle, string supplies)
        {
            SourceName = sourceName;
            LinkTitle = linkTitle;
            Supplies = supplies;
            LinkedGames = Array.Empty<GameSearchResult>();
            StatusText = string.Empty;
            ChangeCommand = new RelayCommand(_ => { }, _ => false);
            ClearCommand = new RelayCommand(_ => { }, _ => false);
            OpenMatchesCommand = new RelayCommand(_ => { }, _ => false);
        }

        /// <summary>A read-only row for a choice another source serves: its name, "Uses TrueGaming" and the fields.</summary>
        internal static MetadataLinkRow Routed(string sourceName, string servedByText, string supplies) =>
            new MetadataLinkRow(sourceName, servedByText, supplies) { IsEditable = false };

        /// <summary>
        /// A row for a choice another source serves from one page (Xbox points from TrueAchievements):
        /// the page and its console, a search that picks another page for this choice alone, and a
        /// reset back to the page the source's own link leads to.
        /// </summary>
        internal static MetadataLinkRow ServedPage(
            MetadataLinksViewModel owner,
            Guid gameId,
            string servicingProviderKey,
            string pageKey,
            string choiceKey,
            string servedByKey,
            string sourceName,
            string linkTitle,
            string supplies,
            string servedByText,
            bool picked,
            IReadOnlyList<GameSearchResult> linkedGames,
            MetadataPageLinkItem page)
        {
            return new MetadataLinkRow(owner, gameId, servicingProviderKey, pageKey, choiceKey, servedByKey, sourceName, linkTitle, supplies, servedByText, picked, linkedGames)
            {
                PageLinks = page == null ? Array.Empty<MetadataPageLinkItem>() : new[] { page }
            };
        }

        private MetadataLinkRow(
            MetadataLinksViewModel owner,
            Guid gameId,
            string servicingProviderKey,
            string pageKey,
            string choiceKey,
            string servedByKey,
            string sourceName,
            string linkTitle,
            string supplies,
            string servedByText,
            bool picked,
            IReadOnlyList<GameSearchResult> linkedGames)
        {
            GameId = gameId;
            ServicingProviderKey = servicingProviderKey;
            SourceKey = pageKey;
            ServedChoiceKey = choiceKey;
            ServedByKey = servedByKey;
            SourceName = sourceName;
            LinkTitle = linkTitle;
            Supplies = supplies;
            LinkDetails = servedByText;
            LinkedGames = linkedGames ?? Array.Empty<GameSearchResult>();
            HasLink = LinkedGames.Count > 0;
            StatusText = string.Empty;
            IsUserSet = picked;
            CanReset = picked;
            ChangeCommand = new RelayCommand(_ => owner.ChangeServedPage(this));
            ClearCommand = new RelayCommand(_ => owner.Clear(this), _ => CanReset);
            OpenMatchesCommand = new RelayCommand(_ => { }, _ => false);
        }

        /// <summary>For a served-page row, the choice it picks the page for ("Xbox"); null otherwise.</summary>
        internal string ServedChoiceKey { get; }

        /// <summary>For a served-page row, the source that reads the page ("TrueGaming"); null otherwise.</summary>
        internal string ServedByKey { get; }

        /// <summary>False for a row that only names the source serving the choice; that source's row holds the link.</summary>
        public bool IsEditable { get; private set; } = true;

        internal MetadataLinkRow(
            MetadataLinksViewModel owner,
            Guid gameId,
            string servicingProviderKey,
            string sourceKey,
            string sourceName,
            string supplies,
            MetadataLinkRecord record,
            MetadataCoverage coverage,
            MetadataSourceSnapshot snapshot,
            IReadOnlyList<GameSearchResult> linkedGames,
            string linkTitle,
            bool canSearch,
            bool stored)
        {
            GameId = gameId;
            ServicingProviderKey = servicingProviderKey;
            SourceKey = sourceKey;
            SourceName = string.IsNullOrWhiteSpace(sourceName) ? sourceKey : sourceName;
            Supplies = supplies ?? string.Empty;
            Snapshot = snapshot;
            StatusToolTip = BuildListStatus(snapshot);
            StatusText = BuildStatus(record, coverage);
            HasLink = record != null && !record.IsNoMatch;
            LinkedGames = linkedGames ?? Array.Empty<GameSearchResult>();
            LinkTitle = HasLink && !string.IsNullOrWhiteSpace(linkTitle)
                ? linkTitle
                : ResourceProvider.GetString("LOCPlayAch_Common_Status_NotLinked");
            // A stored "no match" (the retired Enabled switch's off) is the user's too, so Reset
            // hands it back to matching.
            IsUserSet = record?.Origin == MetadataLinkOrigin.User;
            ShowFixMatches = HasMatches;
            // Select pins the games picked; turning a source off is the game's "Own" choice for its fields.
            ChangeCommand = new RelayCommand(_ => owner.Change(this), _ => canSearch);
            // Any stored link can be dropped: one the user set, a stored no-match, or one matching
            // found, which the next refresh then looks for again. A link the game implies is not stored.
            CanReset = stored;
            ClearCommand = new RelayCommand(_ => owner.Clear(this), _ => CanReset);
            OpenMatchesCommand = new RelayCommand(_ => owner.OpenMatches(this), _ => HasMatches);
        }

        public Guid GameId { get; }

        internal string ServicingProviderKey { get; }

        public string SourceKey { get; }

        public string SourceName { get; }

        /// <summary>The linked games' titles ("Mass Effect, Mass Effect 2"), or "Not linked".</summary>
        public string LinkTitle { get; }

        /// <summary>The link's ids as the source shows them, one per line, for the title's tooltip; null with no link.</summary>
        public string LinkIdsText { get; internal set; }

        /// <summary>The games the link names, offered as the search's current pick.</summary>
        internal IReadOnlyList<GameSearchResult> LinkedGames { get; }

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
        /// Reset drops the stored link, so matching looks again on the next refresh: one the user
        /// entered, a stored "no match", or one matching found.
        /// </summary>
        public bool CanReset { get; }

        /// <summary>The source's list and matches from the last refresh; null before one, or for a capstone-only source.</summary>
        internal MetadataSourceSnapshot Snapshot { get; }

        public bool HasMatches => Snapshot?.Rows.Count > 0;

        /// <summary>Offered whenever the source has a match list from the last refresh, to change any pairing.</summary>
        public bool ShowFixMatches { get; }

        /// <summary>Opens the source's game search to pick the linked game, or games.</summary>
        public ICommand ChangeCommand { get; }

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
