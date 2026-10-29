using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Workshop;
using PlayniteAchievements.Services.Workshop.Preview;
using PlayniteAchievements.ViewModels.Library;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.Workshop
{
    public enum WorkshopSort
    {
        MostInstalled,
        MostLiked,
        Newest,
        Name
    }

    /// <summary>A kind filter entry: null kind means every kind.</summary>
    public sealed class WorkshopKindOption
    {
        public WorkshopKindOption(WorkshopItemKind? kind, string label)
        {
            Kind = kind;
            Label = label;
        }

        public WorkshopItemKind? Kind { get; }

        public string Label { get; }
    }

    public sealed class WorkshopSortOption
    {
        public WorkshopSortOption(WorkshopSort sort, string label)
        {
            Sort = sort;
            Label = label;
        }

        public WorkshopSort Sort { get; }

        public string Label { get; }
    }

    /// <summary>
    /// The Workshop window: loads the index, filters and sorts it, resolves local state per item
    /// (installed, update available, matching library game, published by this install), and
    /// drives install. All collection work happens on the UI thread; network and disk work is awaited.
    /// </summary>
    public sealed class WorkshopViewModel : ObservableObject, IDisposable
    {
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly WorkshopClient _client;
        private readonly WorkshopIdentityStore _identity;
        private readonly Services.Library.LibraryStore _library;
        private readonly Services.Library.GameDataLinkService _gameData;
        private readonly WorkshopInstaller _installer;
        private readonly WorkshopGameMatcher _matcher;
        private readonly WorkshopStatsStore _stats;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, WorkshopGameMatch> _matches = new ConcurrentDictionary<string, WorkshopGameMatch>(StringComparer.OrdinalIgnoreCase);

        private string _searchText = string.Empty;
        private string _tagFilterText = string.Empty;
        private IReadOnlyList<string> _tagFilter = Array.Empty<string>();
        private bool _excludeTags;
        private WorkshopKindOption _selectedKind;
        private WorkshopSortOption _selectedSort;
        private bool _onlyMyGames = true;
        private bool _onlyMine;
        private WorkshopItemViewModel _selectedItem;
        private bool _isLoading;
        private bool _isBusy;
        private string _statusMessage;
        private string _errorMessage;
        private double _progressFraction;
        private Guid? _focusGameId;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private bool _sourceRefreshQueued;
        private bool _applyingSourceChange;

        public WorkshopViewModel(PlayniteAchievementsPlugin plugin, ILogger logger, Guid? focusGameId, WorkshopItemKind? focusKind = null)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _client = plugin.WorkshopClient;
            _identity = plugin.WorkshopIdentityStore;
            _library = plugin.LibraryStore;
            _gameData = plugin.GameDataLinks;
            _installer = plugin.WorkshopInstaller;
            _matcher = plugin.CreateWorkshopGameMatcher();
            _focusGameId = focusGameId;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _stats = plugin.WorkshopStatsStore;
            _identity.Changed += Source_Changed;
            _library.Changed += Source_Changed;
            _gameData.Links.Changed += Source_Changed;
            _stats.StatsChanged += Stats_Changed;
            // The counts are fetched alongside the index rather than after it, so the hearts are
            // up by the time the rows are.
            _ = _stats.EnsureLoadedAsync(_lifetime.Token);

            KindOptions = new List<WorkshopKindOption>
            {
                new WorkshopKindOption(null, ResourceProvider.GetString("LOCPlayAch_Common_All")),
                new WorkshopKindOption(WorkshopItemKind.Colors, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.Colors)),
                new WorkshopKindOption(WorkshopItemKind.NotificationStyle, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.NotificationStyle)),
                new WorkshopKindOption(WorkshopItemKind.ScreenshotFrame, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.ScreenshotFrame)),
                new WorkshopKindOption(WorkshopItemKind.ShowcasePage, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.ShowcasePage)),
                new WorkshopKindOption(WorkshopItemKind.UnlockSounds, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.UnlockSounds)),
                new WorkshopKindOption(WorkshopItemKind.Bundle, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.Bundle)),
                new WorkshopKindOption(WorkshopItemKind.GameCustomData, WorkshopItemViewModel.KindLabelFor(WorkshopItemKind.GameCustomData))
            };
            SortOptions = new List<WorkshopSortOption>
            {
                new WorkshopSortOption(WorkshopSort.MostInstalled, ResourceProvider.GetString("LOCPlayAch_Workshop_SortMostInstalled")),
                new WorkshopSortOption(WorkshopSort.MostLiked, ResourceProvider.GetString("LOCPlayAch_Workshop_SortMostLiked")),
                new WorkshopSortOption(WorkshopSort.Newest, ResourceProvider.GetString("LOCPlayAch_Workshop_SortNewest")),
                new WorkshopSortOption(WorkshopSort.Name, ResourceProvider.GetString("LOCPlayAch_Common_Name"))
            };
            // Opened from the window that owns one kind (or from a game): that kind alone is
            // offered, bundles carrying the part still list under it, and the selector hides.
            var focusedKind = focusGameId.HasValue ? WorkshopItemKind.GameCustomData : focusKind;
            if (focusedKind is WorkshopItemKind only)
            {
                KindOptions = KindOptions.Where(option => option.Kind == only).ToList();
            }

            _selectedKind = focusGameId.HasValue
                ? KindOptions.Last()
                : KindOptions.FirstOrDefault(option => option.Kind == focusKind) ?? KindOptions[0];
            _selectedSort = SortOptions[0];

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.Filter = FilterItem;

            RefreshCommand = new AsyncCommand(async _ => await LoadAsync());
            InstallCommand = new AsyncCommand(async parameter => await InstallAsync(parameter as WorkshopItemViewModel ?? SelectedItem), _ => !IsBusy);
            PreviewCommand = new AsyncCommand(async parameter => await PreviewAsync(parameter as WorkshopItemViewModel ?? SelectedItem), _ => !IsBusy);
            OpenFolderCommand = new RelayCommand(parameter => OpenUrl((parameter as WorkshopItemViewModel ?? SelectedItem)?.FolderUrl));
            ReportCommand = new RelayCommand(parameter => Report(parameter as WorkshopItemViewModel ?? SelectedItem));
            RemoveCommand = new AsyncCommand(async parameter => await RemoveAsync(parameter as WorkshopItemViewModel ?? SelectedItem), _ => !IsBusy);
            EditCommand = new RelayCommand(parameter => Edit(parameter as WorkshopItemViewModel ?? SelectedItem), _ => !IsBusy);
        }

        public ObservableCollection<WorkshopItemViewModel> Items { get; } = new ObservableCollection<WorkshopItemViewModel>();
        public ICollectionView ItemsView { get; }
        public IReadOnlyList<WorkshopKindOption> KindOptions { get; }

        /// <summary>False when the window was opened for one kind, so the kind selector is hidden.</summary>
        public bool ShowKindFilter => KindOptions.Count > 1;

        /// <summary>The one kind this window is scoped to, or null when it browses everything.</summary>
        private WorkshopItemKind? FocusedKind => KindOptions.Count == 1 ? KindOptions[0].Kind : null;
        public IReadOnlyList<WorkshopSortOption> SortOptions { get; }

        public AsyncCommand RefreshCommand { get; }
        public AsyncCommand InstallCommand { get; }
        public AsyncCommand PreviewCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand ReportCommand { get; }
        public AsyncCommand RemoveCommand { get; }
        public RelayCommand EditCommand { get; }

        public WorkshopIdentityStore IdentityStore => _identity;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty, nameof(SearchText)))
                {
                    ItemsView.Refresh();
                }
            }
        }

        /// <summary>Comma-separated tags matched exactly against each item's tags.</summary>
        public string TagFilterText
        {
            get => _tagFilterText;
            set
            {
                if (SetValueAndReturn(ref _tagFilterText, value ?? string.Empty, nameof(TagFilterText)))
                {
                    var previous = _tagFilter;
                    _tagFilter = WorkshopTags.Parse(_tagFilterText);
                    if (!previous.SequenceEqual(_tagFilter))
                    {
                        ItemsView.Refresh();
                    }
                }
            }
        }

        /// <summary>False lists only items with any filter tag; true hides them.</summary>
        public bool ExcludeTags
        {
            get => _excludeTags;
            set
            {
                if (SetValueAndReturn(ref _excludeTags, value, nameof(ExcludeTags)) && _tagFilter.Count > 0)
                {
                    ItemsView.Refresh();
                }
            }
        }

        public WorkshopKindOption SelectedKind
        {
            get => _selectedKind;
            set
            {
                if (SetValueAndReturn(ref _selectedKind, value, nameof(SelectedKind)))
                {
                    OnPropertyChanged(nameof(ShowOnlyMyGames));
                    ApplySort();
                    ItemsView.Refresh();
                }
            }
        }

        public WorkshopSortOption SelectedSort
        {
            get => _selectedSort;
            set
            {
                if (SetValueAndReturn(ref _selectedSort, value, nameof(SelectedSort)))
                {
                    ApplySort();
                }
            }
        }

        public bool OnlyMyGames
        {
            get => _onlyMyGames;
            set
            {
                if (SetValueAndReturn(ref _onlyMyGames, value, nameof(OnlyMyGames)))
                {
                    ItemsView.Refresh();
                }
            }
        }

        /// <summary>
        /// Lists only the items this install published, of every kind; game data among them shows
        /// whether or not its game is in the library.
        /// </summary>
        public bool OnlyMine
        {
            get => _onlyMine;
            set
            {
                if (SetValueAndReturn(ref _onlyMine, value, nameof(OnlyMine)))
                {
                    OnPropertyChanged(nameof(ShowOnlyMyGames));
                    ItemsView.Refresh();
                }
            }
        }

        public bool ShowOnlyMyGames => !_onlyMine && (_selectedKind?.Kind == null || _selectedKind.Kind == WorkshopItemKind.GameCustomData);

        public WorkshopItemViewModel SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetValueAndReturn(ref _selectedItem, value, nameof(SelectedItem)))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    _ = LoadDetailsAsync(value);
                }
            }
        }

        public bool HasSelection => _selectedItem != null;

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetValue(ref _isLoading, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetValueAndReturn(ref _isBusy, value, nameof(IsBusy)))
                {
                    InstallCommand.RaiseCanExecuteChanged();
                    PreviewCommand.RaiseCanExecuteChanged();
                    RemoveCommand?.RaiseCanExecuteChanged();
                    EditCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (SetValueAndReturn(ref _statusMessage, value, nameof(StatusMessage)))
                {
                    OnPropertyChanged(nameof(HasStatus));
                }
            }
        }

        public bool HasStatus => !string.IsNullOrWhiteSpace(_statusMessage);

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetValueAndReturn(ref _errorMessage, value, nameof(ErrorMessage)))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

        public double ProgressFraction
        {
            get => _progressFraction;
            private set => SetValue(ref _progressFraction, value);
        }

        // ---- loading -----------------------------------------------------------------------

        public async Task LoadAsync()
        {
            if (IsLoading)
            {
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            try
            {
                var index = await _client.FetchIndexAsync(_lifetime.Token);

                // Matching game data against the library and checking what is still installed
                // read the achievement cache and disk; that runs off the UI thread, on rows
                // nothing is bound to yet, against one snapshot of the library.
                _matcher.Reset();
                _matches.Clear();
                var rows = await Task.Run(() =>
                {
                    var built = new List<WorkshopItemViewModel>();
                    var library = ReadLibrary();
                    var gameData = ReadGameData();
                    var owner = _identity.TryGetSubmitterHash();
                    foreach (var item in index.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        var row = new WorkshopItemViewModel(item) { Stats = _stats };
                        ApplyLocalState(row, library, gameData, owner);
                        built.Add(row);
                    }

                    // The index marks every item with its publisher's hash. Records of a first
                    // submission learn their published id from this install's items here, as the
                    // share dialog links them, so a later share can update the item.
                    if (owner != null)
                    {
                        _identity.LinkSubmissions(index.Items.Where(item => WorkshopIdentityStore.IsOwnedBy(item, owner)));
                    }

                    return built;
                }, _lifetime.Token);

                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                ApplySort();
                _ = PrefetchPreviewsAsync(rows);
                _ = LoadStatsAsync(index.Items);

                if (_focusGameId.HasValue)
                {
                    // The item the game has applied, else the one that matches it.
                    var applied = _gameData.Get(_focusGameId.Value);
                    var focused = Items.FirstOrDefault(row => row.Kind == WorkshopItemKind.GameCustomData
                                                              && Services.Library.GameDataLinkService.Names(applied, row.Id))
                                  ?? Items.FirstOrDefault(row => _matches.TryGetValue(row.Id, out var match) && match.PlayniteGameId == _focusGameId.Value);
                    if (focused != null)
                    {
                        SearchText = focused.GameName ?? string.Empty;
                        SelectedItem = focused;
                    }
                    else
                    {
                        var game = _plugin.PlayniteApi?.Database?.Games?.Get(_focusGameId.Value);
                        SearchText = game?.Name ?? string.Empty;
                    }

                    _focusGameId = null;
                }

                ItemsView.Refresh();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed loading the Workshop index.");
                ErrorMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_LoadFailed"), ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>The library's Workshop items by Workshop item id, read once per pass over the rows.</summary>
        private ILookup<string, Services.Library.LibraryItem> ReadLibrary()
        {
            try
            {
                return _library.Items
                    .Where(item => item.IsWorkshop && !string.IsNullOrWhiteSpace(item.WorkshopItemId))
                    .ToLookup(item => item.WorkshopItemId, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed reading the library for the Workshop list.");
                return Enumerable.Empty<Services.Library.LibraryItem>().ToLookup(item => item.WorkshopItemId, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Every game's Workshop game data record, read once per pass over the rows.</summary>
        private IReadOnlyDictionary<Guid, LibraryLink> ReadGameData()
        {
            try
            {
                return _gameData.All;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed reading the games' Workshop game data for the Workshop list.");
                return new Dictionary<Guid, LibraryLink>();
            }
        }

        /// <summary>
        /// The game a game data row stands for: a match by provider identity, else a game that
        /// already has the item applied (the matched game first, then the latest apply), else
        /// none. Its record gives the row its installed and update state, and an install goes there.
        /// </summary>
        private Guid? LinkedOrCertainGame(WorkshopItemViewModel row, IReadOnlyDictionary<Guid, LibraryLink> gameData)
        {
            _matches.TryGetValue(row.Id, out var match);
            if (match != null && match.Confidence == WorkshopGameMatchConfidence.Provider)
            {
                return match.PlayniteGameId;
            }

            return gameData
                .Where(pair => Services.Library.GameDataLinkService.Names(pair.Value, row.Id))
                .OrderByDescending(pair => match != null && pair.Key == match.PlayniteGameId)
                .ThenByDescending(pair => pair.Value.AppliedUtc)
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault();
        }

        /// <summary>
        /// Marks a row as in the library when any part of it is a library item, with an update
        /// when the index carries a newer version than the library holds. Game data is not a
        /// library item: it is installed when the game it stands for has it applied, with an
        /// update when the index is newer than that game's version. Marks it as this install's
        /// when its owner hash is <paramref name="owner"/>.
        /// </summary>
        private void ApplyLocalState(
            WorkshopItemViewModel row,
            ILookup<string, Services.Library.LibraryItem> library,
            IReadOnlyDictionary<Guid, LibraryLink> gameData,
            string owner)
        {
            row.IsMine = WorkshopIdentityStore.IsOwnedBy(row.Item, owner);
            if (row.Kind == WorkshopItemKind.GameCustomData)
            {
                var match = _matcher.Match(row.Item.Game?.Keys);
                if (match != null)
                {
                    _matches[row.Id] = match;
                }
                else
                {
                    _matches.TryRemove(row.Id, out _);
                }

                var gameId = LinkedOrCertainGame(row, gameData) ?? match?.PlayniteGameId;
                row.LocalGameName = gameId == null
                    ? null
                    : gameId == match?.PlayniteGameId
                        ? match.GameName
                        : _plugin.PlayniteApi?.Database?.Games?.Get(gameId.Value)?.Name;

                var applied = gameData
                    .Where(pair => Services.Library.GameDataLinkService.Names(pair.Value, row.Id))
                    .ToList();
                row.IsInstalled = applied.Count > 0;
                row.HasUpdate = Services.Library.GameDataLinkService.GamesBehind(applied, row.Item).Count > 0;
                row.Uses = applied
                    .Select(pair =>
                    {
                        var use = new Services.Library.LibraryTargetUse(
                            Services.Library.LibraryTargetKeys.GameData(pair.Key),
                            pair.Value,
                            isEdited: false,
                            Services.Library.GameDataLinkService.HasUpdate(pair.Value, row.Item),
                            canReset: true);
                        var item = new Services.Library.LibraryItem
                        {
                            Kind = Services.Library.LibraryItemKind.GameData,
                            Origin = Services.Library.LibraryItemOrigin.Workshop,
                            WorkshopItemId = row.Id,
                            Version = pair.Value.AppliedVersion
                        };
                        return new WorkshopUseLine(LibraryUseLabels.GameName(_plugin, pair.Key), LibraryUseLabels.State(item, use));
                    })
                    .OrderBy(use => use.Label, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return;
            }

            var owned = library[row.Id].ToList();
            row.IsInstalled = owned.Count > 0;
            var updates = _plugin.LibraryUpdateService;
            var uses = updates == null
                ? new List<WorkshopUseLine>()
                : owned
                    .SelectMany(item => updates.UsesOf(item)
                        .Select(use => new WorkshopUseLine(LibraryUseLabels.Target(_plugin, use.TargetKey), LibraryUseLabels.State(item, use))))
                    .ToList();
            row.IsApplied = uses.Count > 0;
            row.Uses = uses;
            row.HasUpdate = owned.Any(item => WorkshopIdentityStore.IsNewer(row.Version, item.Version));
        }

        /// <summary>
        /// Whether a row belongs under a kind filter: its own kind, or a bundle carrying that
        /// kind as a part ("also in bundles"). Null matches everything.
        /// </summary>
        private static bool MatchesKind(WorkshopItemViewModel row, WorkshopItemKind? kind)
        {
            if (!(kind is WorkshopItemKind wanted) || row.Kind == wanted)
            {
                return true;
            }

            return row.Kind == WorkshopItemKind.Bundle
                && BundlePartFor(wanted) is BundleParts part
                && WorkshopInstaller.BundlePartsOf(row.Item).HasFlag(part);
        }

        /// <summary>
        /// Fetches every row's preview and cover in the background, a few rows at a time, so the
        /// list shows its images without each item having to be selected first. Cached files
        /// return at once; the detail pane's own fetch finds them already there.
        /// </summary>
        private async Task PrefetchPreviewsAsync(IReadOnlyList<WorkshopItemViewModel> rows)
        {
            var pending = rows
                .Where(row => NeedsPreview(row) || NeedsCover(row))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            using (var gate = new SemaphoreSlim(4))
            {
                var fetches = pending.Select(async row =>
                {
                    await gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        if (NeedsCover(row))
                        {
                            var cover = await _client.FetchCoverAsync(row.Item, _lifetime.Token).ConfigureAwait(false);
                            if (cover != null)
                            {
                                row.CoverPath = cover;
                            }
                        }

                        if (NeedsPreview(row))
                        {
                            var path = await _client.FetchPreviewAsync(row.Item, _lifetime.Token).ConfigureAwait(false);
                            if (path != null)
                            {
                                row.PreviewPath = path;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"Failed prefetching the Workshop preview for {row.Id}.");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                try
                {
                    await Task.WhenAll(fetches).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private static bool NeedsPreview(WorkshopItemViewModel row)
            => row.PreviewPath == null && !string.IsNullOrWhiteSpace(row.Item?.Urls?.Preview);

        private static bool NeedsCover(WorkshopItemViewModel row)
            => row.CoverPath == null && !string.IsNullOrWhiteSpace(row.Item?.Urls?.Cover);

        private async Task LoadDetailsAsync(WorkshopItemViewModel row)
        {
            if (row == null)
            {
                return;
            }

            try
            {
                if (NeedsCover(row))
                {
                    row.CoverPath = await _client.FetchCoverAsync(row.Item, _lifetime.Token);
                }

                if (row.PreviewPath == null)
                {
                    row.PreviewPath = await _client.FetchPreviewAsync(row.Item, _lifetime.Token);
                }

                if (row.Readme == null)
                {
                    // The description already shows above the README; an empty result hides it.
                    row.Readme = await _client.FetchReadmeAsync(row.Item, _lifetime.Token) ?? string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed loading Workshop details for {row.Id}.");
            }
        }

        // ---- filter and sort ---------------------------------------------------------------

        private bool FilterItem(object value)
        {
            if (!(value is WorkshopItemViewModel row))
            {
                return false;
            }

            // A kind tab also lists the bundles that carry that part ("also in bundles"), so a user
            // looking for sounds sees sound packs first and bundles containing sounds after them.
            if (!MatchesKind(row, _selectedKind?.Kind))
            {
                return false;
            }

            if (_onlyMine)
            {
                if (!row.IsMine)
                {
                    return false;
                }
            }
            else if (_onlyMyGames && row.Kind == WorkshopItemKind.GameCustomData && !row.IsInLibrary)
            {
                return false;
            }

            if (!WorkshopTags.Matches(row.TagSet, _tagFilter, _excludeTags))
            {
                return false;
            }

            var query = _searchText.Trim();
            return query.Length == 0 || row.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The bundle part a kind tab corresponds to, or null for kinds bundles never carry.</summary>
        public static BundleParts? BundlePartFor(WorkshopItemKind kind)
        {
            switch (kind)
            {
                case WorkshopItemKind.Colors: return BundleParts.Colors;
                case WorkshopItemKind.UnlockSounds: return BundleParts.Sounds;
                case WorkshopItemKind.NotificationStyle: return BundleParts.Toast;
                case WorkshopItemKind.ScreenshotFrame: return BundleParts.Frame;
                default: return null;
            }
        }

        /// <summary>
        /// When a bundle is installed from a part's tab, the part picker starts with just that
        /// part ticked; from the Bundles or All tab every available part is ticked.
        /// </summary>
        public BundleParts PreferredBundleParts =>
            _selectedKind?.Kind is WorkshopItemKind kind && BundlePartFor(kind) is BundleParts part
                ? part
                : BundleParts.All;

        // ---- likes -------------------------------------------------------------------------

        /// <summary>
        /// Reads the like listing and likes this install's own items. A service that cannot be
        /// reached simply leaves the counts and the button out of the list.
        /// </summary>
        private async Task LoadStatsAsync(IEnumerable<WorkshopItem> items)
        {
            try
            {
                if (await _stats.EnsureLoadedAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    await _stats.LikeOwnItemsAsync(items, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Failed reading the Workshop statistics: {ex.Message}");
            }
        }

        /// <summary>
        /// Raises the like properties of the rows a change touched, on the UI thread: a press is
        /// answered off it. A null id means the whole listing arrived, which is also the only
        /// moment the view re-sorts, so a press never moves rows under the pointer.
        /// </summary>
        private void Stats_Changed(object sender, string itemId)
        {
            void Apply()
            {
                foreach (var row in Items)
                {
                    if (itemId == null || string.Equals(row.Id, itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        row.RaiseStatsChanged();
                    }
                }

                if (itemId == null && (_selectedSort?.Sort ?? WorkshopSort.MostInstalled) == WorkshopSort.MostLiked)
                {
                    ItemsView.Refresh();
                }
            }

            if (_dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(Apply));
            }
        }

        private void ApplySort()
        {
            ItemsView.SortDescriptions.Clear();
            // Within a part tab, standalone items of that kind come before the bundles.
            if (_selectedKind?.Kind is WorkshopItemKind kind && kind != WorkshopItemKind.Bundle)
            {
                ItemsView.SortDescriptions.Add(new SortDescription(nameof(WorkshopItemViewModel.IsBundle), ListSortDirection.Ascending));
            }

            switch (_selectedSort?.Sort ?? WorkshopSort.MostInstalled)
            {
                case WorkshopSort.MostLiked:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(WorkshopItemViewModel.LikeCount), ListSortDirection.Descending));
                    break;
                case WorkshopSort.Newest:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(WorkshopItemViewModel.UpdatedDate), ListSortDirection.Descending));
                    break;
                case WorkshopSort.Name:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(WorkshopItemViewModel.Name), ListSortDirection.Ascending));
                    break;
                default:
                    ItemsView.SortDescriptions.Add(new SortDescription(nameof(WorkshopItemViewModel.Installs), ListSortDirection.Descending));
                    break;
            }

            ItemsView.SortDescriptions.Add(new SortDescription(nameof(WorkshopItemViewModel.Name), ListSortDirection.Ascending));
        }

        // ---- install -----------------------------------------------------------------------

        /// <summary>Asks the window to pick bundle parts; null cancels.</summary>
        public Func<WorkshopItemViewModel, Services.Workshop.BundleParts, Services.Workshop.BundleParts?> PickBundleParts { get; set; }

        /// <summary>
        /// Asks the window which games game data goes onto and how each meets its data: the new
        /// targets, empty when nothing new was picked, or null on Cancel.
        /// </summary>
        public Func<WorkshopItemViewModel, IReadOnlyList<Services.Library.GameDataTarget>> PickGameDataTargets { get; set; }

        /// <summary>Asks the window to confirm a question; defaults to yes when unset.</summary>
        public Func<string, bool> Confirm { get; set; }

        /// <summary>True when the game's Workshop game data record is this row's item.</summary>
        private bool IsAppliedTo(WorkshopItemViewModel row, Guid gameId)
        {
            return Services.Library.GameDataLinkService.Names(_gameData.Get(gameId), row.Id);
        }

        public async Task InstallAsync(WorkshopItemViewModel row)
        {
            if (row == null || IsBusy || !row.CanInstall)
            {
                return;
            }

            if (row.Kind == WorkshopItemKind.GameCustomData)
            {
                await InstallGameDataAsync(row);
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            var scratch = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "WorkshopInstall", Guid.NewGuid().ToString("N"));
            var isUpdate = row.HasUpdate;
            try
            {
                var request = new WorkshopInstallRequest { Item = row.Item };

                if (row.Kind == WorkshopItemKind.Bundle)
                {
                    var available = WorkshopInstaller.BundlePartsOf(row.Item);
                    var picked = PickBundleParts?.Invoke(row, available) ?? available;
                    if (picked == Services.Workshop.BundleParts.None)
                    {
                        return;
                    }

                    request.Parts = picked;
                }

                // The install consumes its own copy, so the cached download stays for another
                // preview or install of the same version, and this scratch folder stays the only
                // thing the finally below cleans up.
                var downloaded = await EnsureDownloadedAsync(row);
                Directory.CreateDirectory(scratch);
                request.PackagePath = Path.Combine(scratch, row.Item.Package?.File ?? "package.zip");
                var packagePath = request.PackagePath;
                await Task.Run(() => File.Copy(downloaded, packagePath, overwrite: true), _lifetime.Token);

                // An update brings the targets that follow the item along, keeping their edits.
                if (isUpdate)
                {
                    request.FollowerMode = Services.Library.LibraryApplyMode.Merge;
                }

                StatusMessage = ResourceProvider.GetString("LOCPlayAch_Workshop_Installing");
                var result = await _installer.InstallAsync(request, _lifetime.Token);

                ApplyLocalState(row, ReadLibrary(), ReadGameData(), _identity.TryGetSubmitterHash());
                ItemsView.Refresh();

                StatusMessage = DescribeInstall(result, isUpdate);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed installing Workshop item {row.Id}.");
                ErrorMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message);
                StatusMessage = null;
            }
            finally
            {
                ProgressFraction = 0;
                IsBusy = false;
                try
                {
                    if (Directory.Exists(scratch))
                    {
                        Directory.Delete(scratch, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed cleaning the Workshop install scratch folder.");
                }
            }
        }

        /// <summary>
        /// The status line after an install: what went into the library, what the targets that
        /// follow it kept, what takes the new version on the next apply, and any warnings. Null
        /// when there is nothing to say.
        /// </summary>
        internal static string DescribeInstall(WorkshopInstallResult result, bool isUpdate)
        {
            if (result == null)
            {
                return null;
            }

            var notes = new List<string>();
            var names = string.Join(", ", result.PresetNames.Distinct(StringComparer.OrdinalIgnoreCase));
            if (names.Length > 0 && !isUpdate)
            {
                notes.Add(string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_SavedAsPreset"), names));
            }

            if (result.KeptEdits > 0)
            {
                notes.Add(string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_UpdateKeptEdits"), result.KeptEdits));
            }

            if (result.PendingTargets > 0)
            {
                notes.Add(ResourceProvider.GetString("LOCPlayAch_Library_UpdatesWhenApplied"));
            }

            notes.AddRange(result.Warnings);
            if (notes.Count == 0 && isUpdate)
            {
                notes.Add(ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"));
            }

            return notes.Count > 0 ? string.Join("\n", notes) : null;
        }

        /// <summary>
        /// Game data from Browse. With a newer version than some games have, an update merges it
        /// into every game behind, keeping the edits made there since; otherwise the item goes
        /// onto the games the user picks that do not have it yet. Either way the package is
        /// downloaded once.
        /// </summary>
        private async Task InstallGameDataAsync(WorkshopItemViewModel row)
        {
            var isUpdate = row.HasUpdate;
            IReadOnlyList<Services.Library.GameDataTarget> targets;
            if (isUpdate)
            {
                var behind = Services.Library.GameDataLinkService.GamesBehind(ReadGameData(), row.Item);
                var names = string.Join(", ", behind.Select(gameId => LibraryUseLabels.GameName(_plugin, gameId)));
                if (behind.Count == 0
                    || (Confirm != null && !Confirm(string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_UpdateConfirmGameData"), row.Name, names))))
                {
                    return;
                }

                targets = behind
                    .Select(gameId => new Services.Library.GameDataTarget(gameId, WorkshopGameDataInstallMode.KeepEditsSinceInstall))
                    .ToList();
            }
            else
            {
                targets = PickGameDataTargets?.Invoke(row);
                if (targets == null || targets.Count == 0)
                {
                    return;
                }
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                var downloaded = await EnsureDownloadedAsync(row);
                StatusMessage = ResourceProvider.GetString("LOCPlayAch_Workshop_Installing");
                var applier = new Services.Library.GameDataPackageApplier(_plugin.WorkshopClient, _installer, _gameData);
                var result = await applier.InstallManyAsync(row.Item, downloaded, targets, _lifetime.Token);

                ApplyLocalState(row, ReadLibrary(), ReadGameData(), _identity.TryGetSubmitterHash());
                ItemsView.Refresh();
                StatusMessage = DescribeInstall(result, isUpdate) ?? ResourceProvider.GetString("LOCPlayAch_Status_Succeeded");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed installing Workshop game data {row.Id}.");
                ErrorMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message);
                StatusMessage = null;
            }
            finally
            {
                ProgressFraction = 0;
                IsBusy = false;
            }
        }

        // ---- download cache ----------------------------------------------------------------

        /// <summary>The one package download kept between a preview and an install of the same version.</summary>
        private sealed class CachedDownload
        {
            public string ItemId { get; set; }
            public string Sha256 { get; set; }
            public string Path { get; set; }
            public string Directory { get; set; }
        }

        private CachedDownload _cachedDownload;

        /// <summary>
        /// The path of the row's package on disk: the cached download when it is the same item and
        /// package hash and the file still verifies, else a fresh download that replaces the cache.
        /// Reports progress through the status strip. UI thread.
        /// </summary>
        private async Task<string> EnsureDownloadedAsync(WorkshopItemViewModel row)
        {
            var item = row.Item;
            var sha256 = item.Package?.Sha256 ?? string.Empty;
            var cached = _cachedDownload;
            if (cached != null &&
                string.Equals(cached.ItemId, item.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cached.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(cached.Path))
            {
                var cachedPath = cached.Path;
                if (await Task.Run(() => WorkshopClient.VerifyPackage(item, cachedPath), _lifetime.Token))
                {
                    return cachedPath;
                }
            }

            DeleteCachedDownload();

            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", WorkshopPreviewModelBuilder.ScratchFolderLabel, Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, item.Package?.File ?? "package.zip");
            var total = Math.Max(1, item.Package?.SizeBytes ?? 1);
            StatusMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_Downloading"), row.Name);
            ProgressFraction = 0;
            try
            {
                Directory.CreateDirectory(directory);
                await _client.DownloadPackageAsync(
                    item,
                    path,
                    new Progress<long>(received => ProgressFraction = Math.Min(1, (double)received / total)),
                    _lifetime.Token);
            }
            catch
            {
                PortablePackage.TryDeleteDirectory(directory);
                throw;
            }

            _cachedDownload = new CachedDownload { ItemId = item.Id, Sha256 = sha256, Path = path, Directory = directory };
            return path;
        }

        private void DeleteCachedDownload()
        {
            var cached = _cachedDownload;
            _cachedDownload = null;
            if (cached != null)
            {
                PortablePackage.TryDeleteDirectory(cached.Directory);
            }
        }

        // ---- preview -----------------------------------------------------------------------

        /// <summary>
        /// Shows a preview of the package and returns true when the user chose to install it. The
        /// callee owns the model and disposes it. Set by the window; without it a preview is read
        /// and discarded.
        /// </summary>
        public Func<WorkshopItemViewModel, WorkshopPreviewModel, bool> ShowPreview { get; set; }

        /// <summary>
        /// Downloads the row's package (or reuses the cached download), reads it into a preview
        /// model off the UI thread and hands it to <see cref="ShowPreview"/>; installs when the
        /// preview asks for it. Game data is compared against the matched library game only when
        /// the match is certain enough to need no prompt; otherwise the package is listed alone.
        /// </summary>
        public async Task PreviewAsync(WorkshopItemViewModel row)
        {
            if (row == null || IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            WorkshopPreviewModel model = null;
            var install = false;
            try
            {
                var packagePath = await EnsureDownloadedAsync(row);

                var context = WorkshopPreviewContext.FromPlugin(_plugin);
                // The game the row stands for, else a match by name: compared, never prompted for.
                Guid? targetGameId = null;
                if (row.Kind == WorkshopItemKind.GameCustomData)
                {
                    targetGameId = LinkedOrCertainGame(row, ReadGameData());
                    if (targetGameId == null && _matches.TryGetValue(row.Id, out var match) && match.Confidence == WorkshopGameMatchConfidence.Name)
                    {
                        targetGameId = match.PlayniteGameId;
                    }
                }

                var targetGameName = targetGameId is Guid gameIdForName
                    ? _plugin.PlayniteApi?.Database?.Games?.Get(gameIdForName)?.Name ?? row.LocalGameName
                    : null;
                var kind = row.Kind;
                var itemId = row.Id;
                var dataService = _plugin.AchievementDataService;
                var persisted = _plugin.Settings?.Persisted;
                var managedIcons = _plugin.ManagedCustomIconService;
                var baselines = _installer.Baselines;
                // Only an update onto a game that has the item applied keeps the edits made since;
                // any other install applies the package fresh, so its preview compares without one.
                var keepsEdits = targetGameId is Guid appliedTo && IsAppliedTo(row, appliedTo);
                var baselineFile = keepsEdits ? _installer.GameDataBaselineFile(itemId, targetGameId.Value) : null;

                model = await Task.Run(() =>
                {
                    if (targetGameId is Guid gameId)
                    {
                        GameCustomDataFile current = null;
                        context.GameCustomDataStore?.TryLoad(gameId, out current);
                        context.GameDataSource = new GameCustomDataPreviewSource
                        {
                            GameId = gameId,
                            GameName = targetGameName,
                            RawData = dataService?.GetRawGameAchievementData(gameId),
                            CurrentData = dataService?.GetGameAchievementData(gameId),
                            Current = current,
                            Baseline = keepsEdits ? baselines.Load(baselineFile) : null,
                            Persisted = persisted,
                            ManagedCustomIconService = managedIcons
                        };
                    }

                    return WorkshopPreviewModelBuilder.Build(kind, packagePath, context);
                }, _lifetime.Token);

                StatusMessage = null;
                ProgressFraction = 0;
                IsBusy = false;

                var show = ShowPreview;
                if (show != null)
                {
                    var shown = model;
                    model = null;
                    install = show(row, shown);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed previewing Workshop item {row.Id}.");
                ErrorMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message);
                StatusMessage = null;
            }
            finally
            {
                model?.Dispose();
                ProgressFraction = 0;
                IsBusy = false;
            }

            if (install)
            {
                await InstallAsync(row);
            }
        }

        // ---- misc --------------------------------------------------------------------------

        /// <summary>Opens the form for changing the details of an item this install published; set by the view.</summary>
        public Action<WorkshopItem> OpenEdit { get; set; }

        private void Edit(WorkshopItemViewModel row)
        {
            if (row == null || IsBusy || !row.IsMine)
            {
                return;
            }

            OpenEdit?.Invoke(row.Item);
        }

        /// <summary>Asks the Workshop to take down an item this install published, after a confirmation.</summary>
        private async Task RemoveAsync(WorkshopItemViewModel row)
        {
            if (row == null || IsBusy || !row.IsMine)
            {
                return;
            }

            if (Confirm != null && !Confirm(string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_RemoveConfirm"), row.Name)))
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                var receipt = await _plugin.WorkshopShareService.RemoveAsync(row.Kind, row.Id, _lifetime.Token);
                StatusMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_Share_Submitted"), receipt.IssueUrl);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed removing Workshop item {row.Id}.");
                ErrorMessage = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Report(WorkshopItemViewModel row)
        {
            if (row == null)
            {
                return;
            }

            // The report form lives on the repository; prefill the item id.
            var repo = row.FolderUrl;
            var marker = "/tree/";
            var index = repo?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (index < 0)
            {
                OpenUrl(repo);
                return;
            }

            var repoRoot = repo.Substring(0, index);
            OpenUrl($"{repoRoot}/issues/new?template=report.yml&title={Uri.EscapeDataString("[Report] " + row.Name)}&item_id={Uri.EscapeDataString(row.Id)}");
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed opening {url}.");
            }
        }

        /// <summary>
        /// The library changed (an install, an update or a delete from the Library page), a game's
        /// Workshop game data changed (an install, or an unlink from Manage Achievements), or a
        /// share recorded a submission or the submitter key was replaced: re-check every row once
        /// on the UI thread, so the installed state and Mine follow without reopening the window.
        /// </summary>
        private void Source_Changed(object sender, EventArgs e)
        {
            if (_applyingSourceChange || _sourceRefreshQueued || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _sourceRefreshQueued = true;
            _dispatcher.BeginInvoke(
                new Action(RefreshFromSources),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshFromSources()
        {
            _sourceRefreshQueued = false;
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            _applyingSourceChange = true;
            try
            {
                var library = ReadLibrary();
                var gameData = ReadGameData();
                var owner = _identity.TryGetSubmitterHash();
                foreach (var row in Items.ToList())
                {
                    ApplyLocalState(row, library, gameData, owner);
                }
            }
            finally
            {
                _applyingSourceChange = false;
            }

            ItemsView.Refresh();
        }

        public void Dispose()
        {
            _identity.Changed -= Source_Changed;
            _library.Changed -= Source_Changed;
            _gameData.Links.Changed -= Source_Changed;
            _stats.StatsChanged -= Stats_Changed;
            _matcher.Reset();
            DeleteCachedDownload();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
