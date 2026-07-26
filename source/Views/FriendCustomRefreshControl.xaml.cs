using Playnite.SDK;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views
{
    public partial class FriendCustomRefreshControl : UserControl, INotifyPropertyChanged
    {
        public sealed class ScopeOptionItem
        {
            public FriendRefreshScope Scope { get; set; }
            public string DisplayName { get; set; }
        }

        public sealed class ProviderOptionItem : PlayniteAchievements.Common.ObservableObject
        {
            private bool _isSelected;
            private bool _isEnabled;
            private bool _isAuthenticated;
            private readonly string _enabledAndAuthText;
            private readonly string _disabledText;
            private readonly string _noAuthText;

            public string ProviderKey { get; }
            public string ProviderName { get; }

            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (SetValueAndReturn(ref _isEnabled, value))
                    {
                        OnStateChanged();
                    }
                }
            }

            public bool IsAuthenticated
            {
                get => _isAuthenticated;
                set
                {
                    if (SetValueAndReturn(ref _isAuthenticated, value))
                    {
                        OnStateChanged();
                    }
                }
            }

            public bool IsSelectable => IsEnabled && IsAuthenticated;

            public string StatusText
            {
                get
                {
                    if (!IsEnabled)
                    {
                        return _disabledText;
                    }

                    if (!IsAuthenticated)
                    {
                        return _noAuthText;
                    }

                    return _enabledAndAuthText;
                }
            }

            public bool IsSelected
            {
                get => _isSelected;
                set => SetValue(ref _isSelected, value);
            }

            public ProviderOptionItem(
                string providerKey,
                string providerName,
                bool isEnabled,
                bool isAuthenticated,
                string enabledAndAuthText,
                string disabledText,
                string noAuthText)
            {
                ProviderKey = providerKey;
                ProviderName = providerName;
                _enabledAndAuthText = enabledAndAuthText;
                _disabledText = disabledText;
                _noAuthText = noAuthText;
                _isEnabled = isEnabled;
                _isAuthenticated = isAuthenticated;
            }

            private void OnStateChanged()
            {
                OnPropertyChanged(nameof(IsSelectable));
                OnPropertyChanged(nameof(StatusText));

                if (!IsSelectable && IsSelected)
                {
                    IsSelected = false;
                }
            }
        }

        public sealed class SelectionItem : PlayniteAchievements.Common.ObservableObject
        {
            private bool _isSelected;

            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string ProviderKey { get; set; }
            public List<string> ProviderKeys { get; set; } = new List<string>();
            public List<FriendAccountRef> FriendAccounts { get; set; } = new List<FriendAccountRef>();
            public string ProviderDisplayText { get; set; }

            public string DisplayText => string.IsNullOrWhiteSpace(ProviderDisplayText)
                ? DisplayName
                : $"{DisplayName} ({ProviderDisplayText})";

            public bool IsSelected
            {
                get => _isSelected;
                set => SetValue(ref _isSelected, value);
            }
        }

        private const string WindowPlacementKey = "FriendCustomRefresh";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IPlayniteAPI _api;
        private readonly RefreshRuntime _refreshRuntime;
        private readonly IFriendCacheManager _friendCache;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly FriendsOverviewDataCoordinator _friendsOverviewDataCoordinator;
        private readonly Guid? _selectedGameId;
        private readonly string _selectedGameName;
        private FriendRefreshScope _selectedScope = FriendRefreshScope.Recent;
        private string _summaryText;
        private bool _canRun;
        private string _friendListSearchText;
        private string _gameListSearchText;

        private readonly Dictionary<string, IDataProvider> _providersByKey =
            new Dictionary<string, IDataProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SelectionItem> _allFriendItems = new List<SelectionItem>();
        private readonly List<SelectionItem> _allSharedGameItems = new List<SelectionItem>();
        private readonly ICollectionView _friendView;
        private readonly ICollectionView _sharedGameView;

        public event EventHandler RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        public bool? DialogResult { get; private set; }
        public FriendCustomRefreshOptions ResultOptions { get; private set; }

        public ObservableCollection<ProviderOptionItem> ProviderOptions { get; } =
            new ObservableCollection<ProviderOptionItem>();

        public ObservableCollection<ScopeOptionItem> ScopeOptions { get; } =
            new ObservableCollection<ScopeOptionItem>();

        public ICollectionView FriendItems => _friendView;

        public ICollectionView GameItems => _sharedGameView;

        public FriendRefreshScope SelectedScope
        {
            get => _selectedScope;
            set
            {
                if (_selectedScope == value)
                {
                    return;
                }

                _selectedScope = value;
                OnPropertyChanged(nameof(SelectedScope));
                RecalculateSummary();
            }
        }

        public string FriendListSearchText
        {
            get => _friendListSearchText;
            set
            {
                if (string.Equals(_friendListSearchText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _friendListSearchText = value;
                OnPropertyChanged(nameof(FriendListSearchText));
                _friendView?.Refresh();
            }
        }

        public string GameListSearchText
        {
            get => _gameListSearchText;
            set
            {
                if (string.Equals(_gameListSearchText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _gameListSearchText = value;
                OnPropertyChanged(nameof(GameListSearchText));
                _sharedGameView?.Refresh();
            }
        }

        public string SummaryText
        {
            get => _summaryText;
            private set
            {
                if (string.Equals(_summaryText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _summaryText = value;
                OnPropertyChanged(nameof(SummaryText));
            }
        }

        public bool CanRun
        {
            get => _canRun;
            private set
            {
                if (_canRun == value)
                {
                    return;
                }

                _canRun = value;
                OnPropertyChanged(nameof(CanRun));
            }
        }

        internal FriendCustomRefreshControl(
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            IFriendCacheManager friendCache,
            PlayniteAchievementsSettings settings,
            FriendsOverviewDataCoordinator friendsOverviewDataCoordinator,
            Guid? selectedGameId,
            string selectedGameName)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _refreshRuntime = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _friendCache = friendCache;
            _settings = settings;
            _friendsOverviewDataCoordinator = friendsOverviewDataCoordinator;
            _selectedGameId = selectedGameId.HasValue && selectedGameId.Value != Guid.Empty ? selectedGameId : null;
            _selectedGameName = selectedGameName;

            InitializeComponent();
            DataContext = this;
            InitializeProviders();
            InitializeScopes();
            LoadSelectionData();

            _friendView = CollectionViewSource.GetDefaultView(_allFriendItems);
            _friendView.Filter = FriendFilter;
            _sharedGameView = CollectionViewSource.GetDefaultView(_allSharedGameItems);
            _sharedGameView.Filter = SharedGameFilter;

            RecalculateSummary();
            _ = RefreshProviderAuthAsync();
        }

        internal static bool TryShowDialog(
            IPlayniteAPI api,
            RefreshRuntime refreshRuntime,
            IFriendCacheManager friendCache,
            Action persistSettingsForUi,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            Guid? selectedGameId,
            string selectedGameName,
            FriendsOverviewDataCoordinator friendsOverviewDataCoordinator,
            out FriendCustomRefreshOptions options)
        {
            options = null;
            var control = new FriendCustomRefreshControl(
                api,
                refreshRuntime,
                friendCache,
                settings,
                friendsOverviewDataCoordinator,
                selectedGameId,
                selectedGameName);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_RefreshMode_FriendsCustom"),
                control,
                new WindowOptions
                {
                    Width = 760,
                    Height = 640,
                    CanBeResizable = true,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            window.MinWidth = 620;
            window.MinHeight = 480;
            WindowPlacementPersistenceService.Attach(
                window,
                settings?.Persisted,
                persistSettingsForUi,
                WindowPlacementKey,
                logger);
            control.RequestClose += (_, __) => window.Close();
            window.ShowDialog();

            if (control.DialogResult == true && control.ResultOptions != null)
            {
                options = control.ResultOptions;
                return true;
            }

            return false;
        }

        private void InitializeProviders()
        {
            var readyText = L("LOCPlayAch_CustomRefresh_ProviderStatus_Ready");
            var disabledText = L("LOCPlayAch_Common_Status_Disabled");
            var noAuthText = L("LOCPlayAch_Common_NotAuthenticated");

            foreach (var provider in _refreshRuntime.Providers ?? Array.Empty<IDataProvider>())
            {
                if (provider?.Friends == null || ProviderUiPolicies.ShouldHideFromSetupSurfaces(provider.ProviderKey))
                {
                    continue;
                }

                var isEnabled = _refreshRuntime.ProviderRegistry?.IsProviderEnabled(provider.ProviderKey) ?? true;
                var isAuthenticated = isEnabled && provider.AuthSession == null && provider.IsAuthenticated;
                var option = new ProviderOptionItem(
                    provider.ProviderKey,
                    provider.ProviderName,
                    isEnabled,
                    isAuthenticated,
                    readyText,
                    disabledText,
                    noAuthText)
                {
                    IsSelected = isEnabled && isAuthenticated
                };
                option.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ProviderOptionItem.IsSelected))
                    {
                        _friendView?.Refresh();
                        _sharedGameView?.Refresh();
                        RecalculateSummary();
                    }
                };
                ProviderOptions.Add(option);
                _providersByKey[provider.ProviderKey] = provider;
            }
        }

        private async Task RefreshProviderAuthAsync()
        {
            foreach (var option in ProviderOptions)
            {
                if (!_providersByKey.TryGetValue(option.ProviderKey, out var provider) || provider == null)
                {
                    continue;
                }

                try
                {
                    var isEnabled = _refreshRuntime.ProviderRegistry?.IsProviderEnabled(provider.ProviderKey) ?? true;
                    option.IsEnabled = isEnabled;
                    var wasSelectable = option.IsSelectable;
                    option.IsAuthenticated = isEnabled &&
                        await _refreshRuntime.IsProviderAuthenticatedAsync(provider, CancellationToken.None).ConfigureAwait(true);

                    // Default newly-confirmed providers to selected so the run is ready without re-checking.
                    if (!wasSelectable && option.IsSelectable)
                    {
                        option.IsSelected = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    option.IsAuthenticated = false;
                    Logger?.Warn(ex, $"Failed to refresh friend custom auth state for provider {option.ProviderKey}.");
                }
            }

            _friendView?.Refresh();
            _sharedGameView?.Refresh();
            RecalculateSummary();
        }

        private void InitializeScopes()
        {
            ScopeOptions.Add(new ScopeOptionItem { Scope = FriendRefreshScope.Recent, DisplayName = L("LOCPlayAch_RefreshModeShort_FriendsRecent") });
            if (_settings?.Persisted?.IncludeUnownedFriendGames == true)
            {
                // Full scans unowned friend games; hidden when the global toggle excludes them
                // (the request planner also clamps Full to Shared as a backstop).
                ScopeOptions.Add(new ScopeOptionItem { Scope = FriendRefreshScope.Full, DisplayName = L("LOCPlayAch_RefreshModeShort_FriendsFull") });
            }

            ScopeOptions.Add(new ScopeOptionItem { Scope = FriendRefreshScope.Shared, DisplayName = L("LOCPlayAch_RefreshModeShort_FriendsShared") });
            ScopeOptions.Add(new ScopeOptionItem { Scope = FriendRefreshScope.Installed, DisplayName = L("LOCPlayAch_RefreshModeShort_FriendsInstalled") });
            if (_selectedGameId.HasValue)
            {
                ScopeOptions.Add(new ScopeOptionItem
                {
                    Scope = FriendRefreshScope.SelectedGame,
                    DisplayName = string.Format(
                        L("LOCPlayAch_FriendCustomRefresh_SelectedGameScope"),
                        string.IsNullOrWhiteSpace(_selectedGameName) ? _selectedGameId.Value.ToString("D") : _selectedGameName)
                });
            }
        }

        private void LoadSelectionData()
        {
            try
            {
                var snapshot = LoadSelectionSnapshot();
                if (snapshot != null)
                {
                    foreach (var friend in snapshot.Friends ?? Enumerable.Empty<FriendSummaryItem>())
                    {
                        var selection = CreateFriendSelectionItem(friend);
                        if (selection == null)
                        {
                            continue;
                        }

                        _allFriendItems.Add(selection);
                    }

                    var seenGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var game in snapshot.Games ?? Enumerable.Empty<FriendGameSummaryItem>())
                    {
                        if (game?.PlayniteGameId == null || game.PlayniteGameId == Guid.Empty)
                        {
                            continue;
                        }

                        var id = game.PlayniteGameId.Value.ToString();
                        if (!seenGames.Add(id))
                        {
                            continue;
                        }

                        _allSharedGameItems.Add(new SelectionItem
                        {
                            Id = id,
                            DisplayName = string.IsNullOrWhiteSpace(game.GameName) ? id : game.GameName,
                            ProviderKey = game.ProviderKey,
                            ProviderKeys = NormalizeProviderKeys(new[] { game.ProviderKey })
                        });
                    }
                }

                _allFriendItems.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
                _allSharedGameItems.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            }
            catch (Exception ex)
            {
                Logger?.Warn(ex, "Failed to load friend custom refresh selection data.");
            }
        }

        private FriendsOverviewSnapshot LoadSelectionSnapshot()
        {
            if (_friendsOverviewDataCoordinator?.TryGetCurrentSnapshot(out var snapshot) == true)
            {
                return snapshot;
            }

            var data = _friendCache?.LoadFriendsOverviewData(0);
            return FriendsOverviewDataCoordinator.CreateSnapshot(data, _settings?.Persisted);
        }

        internal static SelectionItem CreateFriendSelectionItem(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                return null;
            }

            var accounts = NormalizeFriendAccounts(friend.MemberAccounts);
            if (accounts.Count == 0 &&
                !string.Equals(friend.ProviderKey, FriendOverviewProjection.MergedProviderKey, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                !string.IsNullOrWhiteSpace(friend.ExternalUserId))
            {
                accounts.Add(FriendAccountRef.From(friend.ProviderKey, friend.ExternalUserId));
            }

            var providerKeys = accounts.Count > 0
                ? NormalizeProviderKeys(accounts.Select(account => account.ProviderKey))
                : NormalizeProviderKeys((friend.MemberProviderKeys ?? new List<string>())
                    .Concat(new[] { friend.ProviderKey }));
            if (providerKeys.Count == 0 || accounts.Count == 0)
            {
                return null;
            }

            var id = !string.IsNullOrWhiteSpace(friend.MergedFriendId)
                ? friend.MergedFriendId
                : friend.ExternalUserId;
            var displayName = string.IsNullOrWhiteSpace(friend.DisplayName)
                ? id
                : friend.DisplayName;

            return new SelectionItem
            {
                Id = id,
                DisplayName = displayName,
                ProviderKey = providerKeys.FirstOrDefault(),
                ProviderKeys = providerKeys,
                FriendAccounts = accounts,
                ProviderDisplayText = ResolveProviderDisplayText(friend, providerKeys)
            };
        }

        private static List<FriendAccountRef> NormalizeFriendAccounts(IEnumerable<FriendAccountRef> accounts)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<FriendAccountRef>();
            foreach (var account in accounts ?? Enumerable.Empty<FriendAccountRef>())
            {
                var next = account?.Clone()?.Normalize();
                if (string.IsNullOrWhiteSpace(next?.Key) || !seen.Add(next.Key))
                {
                    continue;
                }

                normalized.Add(next);
            }

            return normalized;
        }

        private static List<string> NormalizeProviderKeys(IEnumerable<string> providerKeys)
        {
            return (providerKeys ?? Enumerable.Empty<string>())
                .Where(provider => !string.IsNullOrWhiteSpace(provider))
                .Select(provider => provider.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(provider => provider, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string ResolveProviderDisplayText(FriendSummaryItem friend, IReadOnlyList<string> providerKeys)
        {
            if (!string.IsNullOrWhiteSpace(friend?.MemberProviderDisplayText))
            {
                return friend.MemberProviderDisplayText;
            }

            var providers = (providerKeys ?? Array.Empty<string>())
                .Where(provider => !string.IsNullOrWhiteSpace(provider))
                .Select(provider =>
                {
                    var localized = ProviderRegistry.GetLocalizedName(provider);
                    return string.IsNullOrWhiteSpace(localized) ? provider : localized;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return providers.Count == 0 ? null : string.Join(" + ", providers);
        }

        private HashSet<string> GetSelectedProviderKeys()
        {
            return new HashSet<string>(
                ProviderOptions
                    .Where(option => option.IsSelectable && option.IsSelected)
                    .Select(option => option.ProviderKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
        }

        private bool FriendFilter(object item)
        {
            if (!(item is SelectionItem selection))
            {
                return false;
            }

            var providers = GetSelectedProviderKeys();
            if (providers.Count > 0 &&
                !(selection.ProviderKeys ?? new List<string>())
                .Any(provider => providers.Contains(provider)))
            {
                return false;
            }

            return MatchesSearch(selection.DisplayName, _friendListSearchText);
        }

        private bool SharedGameFilter(object item)
        {
            if (!(item is SelectionItem selection))
            {
                return false;
            }

            var providers = GetSelectedProviderKeys();
            if (providers.Count > 0 &&
                !(selection.ProviderKeys ?? new List<string>())
                .Any(provider => providers.Contains(provider)))
            {
                return false;
            }

            return MatchesSearch(selection.DisplayName, _gameListSearchText);
        }

        private static bool MatchesSearch(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return (text ?? string.Empty).IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var providerKeys = ProviderOptions
                .Where(option => option.IsSelectable && option.IsSelected)
                .Select(option => option.ProviderKey)
                .ToList();
            var selectedProviderSet = new HashSet<string>(providerKeys, StringComparer.OrdinalIgnoreCase);

            var friendAccounts = _allFriendItems
                .Where(item => item.IsSelected)
                .SelectMany(item => item.FriendAccounts ?? new List<FriendAccountRef>())
                .Where(account =>
                    account != null &&
                    !string.IsNullOrWhiteSpace(account.ProviderKey) &&
                    !string.IsNullOrWhiteSpace(account.ExternalUserId) &&
                    selectedProviderSet.Contains(account.ProviderKey))
                .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var friendIds = friendAccounts
                .Select(account => account.ExternalUserId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var gameIds = _allSharedGameItems
                .Where(item => item.IsSelected)
                .Select(item => item.Id)
                .Where(id => Guid.TryParse(id, out _))
                .Select(Guid.Parse)
                .Distinct()
                .ToList();

            if (gameIds.Count == 0 && SelectedScope == FriendRefreshScope.SelectedGame && _selectedGameId.HasValue)
            {
                gameIds.Add(_selectedGameId.Value);
            }

            ResultOptions = new FriendCustomRefreshOptions
            {
                ProviderKeys = providerKeys,
                Scope = SelectedScope,
                PlayniteGameIds = gameIds.Count > 0 ? gameIds : null,
                FriendAccounts = friendAccounts.Count > 0 ? friendAccounts : null,
                FriendExternalUserIds = friendIds.Count > 0 ? friendIds : null
            };
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void RecalculateSummary()
        {
            var selectedProviders = ProviderOptions
                .Where(option => option.IsSelectable && option.IsSelected)
                .Select(option => option.ProviderName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            var providerDisplay = selectedProviders.Count == 0
                ? L("LOCPlayAch_Common_None")
                : string.Join(", ", selectedProviders);
            var scopeDisplay = ScopeOptions
                .FirstOrDefault(option => option.Scope == SelectedScope)
                ?.DisplayName ?? SelectedScope.ToString();

            SummaryText = string.Format(
                L("LOCPlayAch_FriendCustomRefresh_SummaryFormat"),
                providerDisplay,
                scopeDisplay);
            CanRun = selectedProviders.Count > 0 &&
                     (SelectedScope != FriendRefreshScope.SelectedGame || _selectedGameId.HasValue);
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
