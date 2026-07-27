using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.Services.StartPage;
using PlayniteAchievements.ViewModels.Items;
using StartPage.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public sealed class StartPageFriendsRecentUnlocksGridViewModel : ObservableObject, IStartPageControl, IDisposable
    {
        private readonly object _refreshLock = new object();
        private readonly FriendsRecentUnlocksDataCoordinator _dataCoordinator;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly SearchTextIndex<FriendAchievementDisplayItem> _searchIndex =
            new SearchTextIndex<FriendAchievementDisplayItem>(item =>
                SearchTextBuilder.FromValues(item?.GameName, item?.FriendName, item?.DisplayName));
        private CancellationTokenSource _refreshCts;
        private PersistedSettings _subscribedPersistedSettings;
        private List<FriendAchievementDisplayItem> _sourceItems = new List<FriendAchievementDisplayItem>();
        private bool _isLoading;
        private bool _disposed;
        private string _statusText;
        private string _searchText = string.Empty;

        internal StartPageFriendsRecentUnlocksGridViewModel(
            FriendsRecentUnlocksDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _dataCoordinator = dataCoordinator ?? throw new ArgumentNullException(nameof(dataCoordinator));
            _settings = settings ?? new PlayniteAchievementsSettings();
            _logger = logger;

            ControlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(SearchText),
                    () => SearchText,
                    value => SearchText = value,
                    L("LOCPlayAch_Filter_Achievements"),
                    () => SearchText = string.Empty)
            };

            _dataCoordinator.SnapshotInvalidated += DataCoordinator_SnapshotInvalidated;
            _settings.PropertyChanged += Settings_PropertyChanged;
            AttachPersistedSettings(_settings.Persisted);
        }

        public BulkObservableCollection<FriendAchievementDisplayItem> Items { get; } =
            new BulkObservableCollection<FriendAchievementDisplayItem>();

        public GridControlBarViewModel ControlBar { get; }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetValue(ref _isLoading, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetValue(ref _statusText, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(_searchText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _searchText = normalized;
                OnPropertyChanged(nameof(SearchText));
                ApplyCurrentItems();
            }
        }

        private PersistedSettings PersistedSettings => _settings?.Persisted;

        private StartPageFriendsRecentUnlocksGridSettings WidgetSettings =>
            PersistedSettings?.StartPageFriendsRecentUnlocksGrid ?? new StartPageFriendsRecentUnlocksGridSettings();

        public bool UseCoverImages => WidgetSettings.UseCoverImages;

        public bool ShowRarityGlow => WidgetSettings.ShowRarityGlow;

        public bool ColorNamesByRarity => WidgetSettings.ColorNamesByRarity;

        public bool ColorRarityColumnsByRarity => WidgetSettings.ColorRarityColumnsByRarity;

        public bool ShowColumnHeaders => WidgetSettings.ShowColumnHeaders;

        public bool ShowControlBar => WidgetSettings.ShowControlBar;

        public double? RowHeight => WidgetSettings.RowHeight;

        public void OnStartPageOpened()
        {
            _ = RefreshAsync(forceRefresh: false);
        }

        public void OnStartPageClosed()
        {
        }

        public void OnDayChanged(DateTime newTime)
        {
            _dataCoordinator.Invalidate();
            _ = RefreshAsync(forceRefresh: true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dataCoordinator.SnapshotInvalidated -= DataCoordinator_SnapshotInvalidated;
            _settings.PropertyChanged -= Settings_PropertyChanged;
            AttachPersistedSettings(null);

            lock (_refreshLock)
            {
                _refreshCts?.Cancel();
                _refreshCts?.Dispose();
                _refreshCts = null;
            }
        }

        private async Task RefreshAsync(bool forceRefresh)
        {
            if (_disposed)
            {
                return;
            }

            if (forceRefresh)
            {
                _dataCoordinator.Invalidate();
            }

            CancellationTokenSource cts;
            lock (_refreshLock)
            {
                _refreshCts?.Cancel();
                _refreshCts = new CancellationTokenSource();
                cts = _refreshCts;
            }

            var recentLimit = GetCoordinatorRecentLimit();
            RunOnUiThread(() =>
            {
                IsLoading = true;
                StatusText = L("LOCPlayAch_Status_LoadingAchievements");
            });

            try
            {
                var snapshot = await _dataCoordinator
                    .GetSnapshotAsync(recentLimit, cts.Token)
                    .ConfigureAwait(false);
                if (cts.IsCancellationRequested || _disposed)
                {
                    return;
                }

                RunOnUiThread(() =>
                {
                    _sourceItems = (snapshot?.RecentUnlocks ?? new List<FriendAchievementDisplayItem>())
                        .Where(item => item != null)
                        .ToList();
                    ApplyCurrentItems();
                    StatusText = string.Empty;
                    IsLoading = false;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to refresh StartPage friends recent achievements widget.");
                RunOnUiThread(() =>
                {
                    StatusText = L("LOCPlayAch_Error_RebuildFailed");
                    IsLoading = false;
                });
            }
        }

        private void ApplyCurrentItems()
        {
            Items.ReplaceAll(StartPageWidgetProjection.ProjectFriendRecentUnlocks(
                StartPageWidgetProjection.FilterFriendRecentUnlocksBySearch(_sourceItems, _searchIndex, SearchText),
                PersistedSettings,
                appearanceSettings: _settings));
        }

        private int GetCoordinatorRecentLimit()
        {
            var settings = WidgetSettings;
            var maxRows = settings.MaxRows;
            if (!maxRows.HasValue || maxRows.Value <= 0)
            {
                return 0;
            }

            return settings.SortMode == CompactListSortMode.UnlockTime && settings.SortDescending
                ? maxRows.Value
                : 0;
        }

        private void DataCoordinator_SnapshotInvalidated(object sender, EventArgs e)
        {
            _ = RefreshAsync(forceRefresh: false);
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                AttachPersistedSettings(_settings.Persisted);
                OnPersistedSettingsChanged(null);
                _ = RefreshAsync(forceRefresh: false);
            }
        }

        private void PersistedSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            OnPersistedSettingsChanged(propertyName);
            if (ShouldRefreshForPersistedSettingsChanged(propertyName))
            {
                _ = RefreshAsync(forceRefresh: false);
            }
        }

        private void OnPersistedSettingsChanged(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.UseCoverImages)))
            {
                OnPropertyChanged(nameof(UseCoverImages));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.ShowRarityGlow)))
            {
                OnPropertyChanged(nameof(ShowRarityGlow));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.ColorNamesByRarity)))
            {
                OnPropertyChanged(nameof(ColorNamesByRarity));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.ColorRarityColumnsByRarity)))
            {
                OnPropertyChanged(nameof(ColorRarityColumnsByRarity));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.ShowColumnHeaders)))
            {
                OnPropertyChanged(nameof(ShowColumnHeaders));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.ShowControlBar)))
            {
                OnPropertyChanged(nameof(ShowControlBar));
            }

            if (string.IsNullOrEmpty(propertyName) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.RowHeight)) ||
                propertyName == nameof(PersistedSettings.StartPageFriendsRecentAchievementsGridRowHeight))
            {
                OnPropertyChanged(nameof(RowHeight));
            }

            if (IsWidgetSettingsProperty(propertyName) &&
                !IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.SortMode)) &&
                !IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.SortDescending)) &&
                !IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.MaxRows)))
            {
                ApplyCurrentItems();
            }
        }

        private static bool ShouldRefreshForPersistedSettingsChanged(string propertyName)
        {
            if (IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.SortMode)) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.SortDescending)) ||
                IsWidgetSettingsProperty(propertyName, nameof(StartPageFriendsRecentUnlocksGridSettings.MaxRows)) ||
                propertyName == nameof(PersistedSettings.StartPageFriendsRecentAchievementsGridMaxRows))
            {
                return true;
            }

            if (propertyName == nameof(PersistedSettings.StartPageFriendAchievementColumnVisibility) ||
                propertyName == nameof(PersistedSettings.StartPageFriendAchievementColumnWidths) ||
                propertyName == nameof(PersistedSettings.StartPageFriendAchievementColumnOrder) ||
                propertyName == nameof(PersistedSettings.StartPageFriendAchievementColumnAlignments) ||
                propertyName == nameof(PersistedSettings.StartPageFriendAchievementColumnVerticalAlignments) ||
                propertyName == nameof(PersistedSettings.StartPageFriendAchievementColumnHeaderAlignments) ||
                propertyName == nameof(PersistedSettings.StartPageFriendsRecentAchievementsGridRowHeight))
            {
                return false;
            }

            return !IsWidgetSettingsProperty(propertyName);
        }

        private void AttachPersistedSettings(PersistedSettings persistedSettings)
        {
            if (ReferenceEquals(_subscribedPersistedSettings, persistedSettings))
            {
                return;
            }

            if (_subscribedPersistedSettings != null)
            {
                _subscribedPersistedSettings.PropertyChanged -= PersistedSettings_PropertyChanged;
            }

            _subscribedPersistedSettings = persistedSettings;
            if (_subscribedPersistedSettings != null)
            {
                _subscribedPersistedSettings.PropertyChanged += PersistedSettings_PropertyChanged;
            }
        }

        private static bool IsWidgetSettingsProperty(string propertyName, string childPropertyName = null)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return true;
            }

            const string prefix = nameof(PersistedSettings.StartPageFriendsRecentUnlocksGrid) + ".";
            if (!propertyName.StartsWith(prefix))
            {
                return propertyName == nameof(PersistedSettings.StartPageFriendsRecentUnlocksGrid);
            }

            return string.IsNullOrEmpty(childPropertyName) ||
                   string.Equals(
                       propertyName.Substring(prefix.Length),
                       childPropertyName,
                       StringComparison.Ordinal);
        }

        private static void RunOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(action);
                return;
            }

            action();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
