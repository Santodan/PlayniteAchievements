using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    internal sealed class FriendsSettingsViewModel : ObservableObject
    {
        private const string ExophaseProviderKey = "Exophase";
        private const string RetroAchievementsProviderKey = "RetroAchievements";
        private const string SteamProviderKey = "Steam";

        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ProviderRegistry _providerRegistry;
        private readonly ILogger _logger;
        private readonly IFriendCacheManager _friendCache;
        private readonly DispatcherTimer _persistDebounceTimer;
        private ExophaseSettings _exophaseSettings;
        private string _manualExophaseUsername;
        private string _statusText;
        private bool _isBusy;
        private bool _hasPendingPersist;
        private string _pendingPersistProviderKey;

        public FriendsSettingsViewModel(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin;
            _providerRegistry = providerRegistry;
            _logger = logger;
            _friendCache = plugin?.RefreshRuntime?.Cache as IFriendCacheManager;

            AutoDiscoverProviders = new ObservableCollection<FriendAutoDiscoverProviderItem>();
            Friends = new ObservableCollection<FriendSettingsPersonRowItem>();
            RefreshAutoDiscoverCommand = new AsyncCommand(_ => RefreshAutoDiscoverAsync(), _ => !IsBusy);
            AddManualFriendCommand = new AsyncCommand(_ => AddManualExophaseFriendAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(ManualExophaseUsername));
            MergeSelectedCommand = new RelayCommand(_ => MergeSelectedFriends(), _ => CanMergeSelectedFriends());
            // Per-row enablement is driven by the IsEnabled bindings (CanUnmerge / CanRemove) in
            // FriendsSettingsTab.xaml. The CanExecute predicate is intentionally left open because
            // this RelayCommand is not wired to CommandManager.RequerySuggested, so a
            // parameter-dependent CanExecute would never re-query after CommandParameter binds and
            // the button would stay disabled. Both execute methods guard their parameter internally.
            UnmergeFriendCommand = new RelayCommand(UnmergeFriend);
            RemoveFriendCommand = new RelayCommand(RemoveFriend);

            _persistDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _persistDebounceTimer.Tick += OnPersistDebounceTimerTick;

            InitializeExophaseSettingsSubscription();
            Initialize();
        }

        public ObservableCollection<FriendAutoDiscoverProviderItem> AutoDiscoverProviders { get; }

        public ObservableCollection<FriendSettingsPersonRowItem> Friends { get; }

        public ICommand RefreshAutoDiscoverCommand { get; }

        public ICommand AddManualFriendCommand { get; }

        public ICommand MergeSelectedCommand { get; }

        public ICommand UnmergeFriendCommand { get; }

        public ICommand RemoveFriendCommand { get; }

        public bool UseExophaseForSteamFriendOwnership
        {
            get => _settings?.Persisted?.UseExophaseForSteamFriendOwnership == true;
            set
            {
                var persisted = _settings?.Persisted;
                if (persisted == null || persisted.UseExophaseForSteamFriendOwnership == value)
                {
                    return;
                }

                persisted.UseExophaseForSteamFriendOwnership = value;
                OnPropertyChanged();
                PersistAndNotify(null);
            }
        }

        public bool IncludeUnownedFriendGames
        {
            get => _settings?.Persisted?.IncludeUnownedFriendGames == true;
            set
            {
                var persisted = _settings?.Persisted;
                if (persisted == null || persisted.IncludeUnownedFriendGames == value)
                {
                    return;
                }

                persisted.IncludeUnownedFriendGames = value;
                OnPropertyChanged();
                // Debounce the persist/sync fan-out (settings serialization, cache sync, theme
                // refresh) so rapid checkbox toggles stay responsive.
                SchedulePersistAndNotify(null);
            }
        }

        public bool IsExophaseProviderEnabled => _exophaseSettings?.IsEnabled == true;

        public string ManualExophaseUsername
        {
            get => _manualExophaseUsername;
            set
            {
                if (SetValueAndReturn(ref _manualExophaseUsername, value))
                {
                    (AddManualFriendCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetValueAndReturn(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatusText));
                }
            }
        }

        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetValueAndReturn(ref _isBusy, value))
                {
                    (RefreshAutoDiscoverCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                    (AddManualFriendCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                    (MergeSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private void InitializeExophaseSettingsSubscription()
        {
            try
            {
                _exophaseSettings = _providerRegistry?.GetSettings<ExophaseSettings>() ??
                                    ProviderRegistry.Settings<ExophaseSettings>();
                if (_exophaseSettings != null)
                {
                    PropertyChangedEventManager.AddHandler(
                        _exophaseSettings,
                        ExophaseSettings_PropertyChanged,
                        nameof(ExophaseSettings.IsEnabled));
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to attach Exophase settings state for Friends settings.");
            }
        }

        private void ExophaseSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || string.Equals(e.PropertyName, nameof(ExophaseSettings.IsEnabled), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsExophaseProviderEnabled));
            }
        }

        private void Initialize()
        {
            var migrated = _settings.Persisted?.MigrateLegacyProviderFriends() == true;
            var seeded = false;
            if (_friendCache != null)
            {
                seeded |= FriendSettingsSyncService.MergeCachedFriends(_settings.Persisted, _friendCache, SteamProviderKey);
                seeded |= FriendSettingsSyncService.MergeCachedFriends(_settings.Persisted, _friendCache, RetroAchievementsProviderKey);
                seeded |= FriendSettingsSyncService.MergeCachedFriends(_settings.Persisted, _friendCache, ExophaseProviderKey, FriendSettingsSource.Manual);
                FriendSettingsSyncService.SyncConfiguredFriendsToCache(_settings.Persisted, _friendCache, _logger);
            }

            BuildAutoDiscoverProviders();
            ApplyExophasePlatformConflicts();
            RebuildFriends();
            if (migrated || seeded)
            {
                PersistAndNotify(null);
            }
        }

        private void BuildAutoDiscoverProviders()
        {
            AutoDiscoverProviders.Clear();
            AutoDiscoverProviders.Add(new FriendAutoDiscoverProviderItem(
                SteamProviderKey,
                ProviderRegistry.GetLocalizedName(SteamProviderKey),
                isAvailable: IsFriendProviderAvailable(SteamProviderKey),
                isSelected: _settings.Persisted?.IsFriendAutoDiscoverEnabled(SteamProviderKey) == true,
                onChanged: OnAutoDiscoverProviderChanged));
            AutoDiscoverProviders.Add(new FriendAutoDiscoverProviderItem(
                RetroAchievementsProviderKey,
                ProviderRegistry.GetLocalizedName(RetroAchievementsProviderKey),
                isAvailable: IsFriendProviderAvailable(RetroAchievementsProviderKey),
                isSelected: _settings.Persisted?.IsFriendAutoDiscoverEnabled(RetroAchievementsProviderKey) == true,
                onChanged: OnAutoDiscoverProviderChanged));
        }

        private bool IsFriendProviderAvailable(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey) ||
                _providerRegistry?.TryGetProvider(providerKey, out var provider) != true ||
                provider?.Friends == null)
            {
                return false;
            }

            if (string.Equals(providerKey, RetroAchievementsProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return provider.IsAuthenticated;
            }

            return true;
        }

        private void RebuildFriends()
        {
            Friends.Clear();
            var persisted = _settings.Persisted;
            if (persisted == null)
            {
                return;
            }

            persisted.FriendMergeGroups = persisted.FriendMergeGroups;
            var entries = (persisted.Friends ?? new ObservableCollection<FriendSettingsEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.ProviderKey) && !string.IsNullOrWhiteSpace(entry.ExternalUserId))
                .ToList();
            var entriesByKey = entries
                .GroupBy(entry => FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var groupedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<FriendSettingsPersonRowItem>();

            foreach (var group in persisted.GetFriendMergeGroups())
            {
                var groupEntries = (group.Members ?? new List<FriendAccountRef>())
                    .Select(member => entriesByKey.TryGetValue(member.Key, out var entry) ? entry : null)
                    .Where(entry => entry != null)
                    .ToList();
                if (groupEntries.Count < 2)
                {
                    continue;
                }

                foreach (var entry in groupEntries)
                {
                    groupedKeys.Add(FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId));
                }

                rows.Add(new FriendSettingsPersonRowItem(
                    group,
                    groupEntries,
                    ResolveDisabledExophasePlatformTokens(groupEntries),
                    OnPersonRowChanged,
                    OnAccountRowChanged,
                    OnPersonSelectionChanged));
            }

            foreach (var entry in entries.Where(entry => !groupedKeys.Contains(FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId))))
            {
                rows.Add(new FriendSettingsPersonRowItem(
                    null,
                    new[] { entry },
                    ResolveDisabledExophasePlatformTokens(new[] { entry }),
                    OnPersonRowChanged,
                    OnAccountRowChanged,
                    OnPersonSelectionChanged));
            }

            foreach (var row in rows
                .OrderBy(row => row.IsIgnored)
                .ThenBy(row => row.SortProviderName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                Friends.Add(row);
            }

            (MergeSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UnmergeFriendCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveFriendCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnAutoDiscoverProviderChanged(FriendAutoDiscoverProviderItem item)
        {
            if (item == null)
            {
                return;
            }

            _settings.Persisted?.SetFriendAutoDiscoverEnabled(item.ProviderKey, item.IsSelected);
            PersistAndNotify(item.ProviderKey);
        }

        private void OnPersonSelectionChanged(FriendSettingsPersonRowItem row)
        {
            (MergeSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPersonRowChanged(FriendSettingsPersonRowItem row)
        {
            if (row == null)
            {
                return;
            }

            row.WritePersonSettings(_settings.Persisted);
            ApplyExophasePlatformConflicts();
            row.RefreshPlatformConflicts(ResolveDisabledExophasePlatformTokens(row.Accounts.Select(account => account.Entry)));
            SchedulePersistAndNotify(null);
        }

        private void OnAccountRowChanged(FriendSettingsAccountItem account)
        {
            if (account == null)
            {
                return;
            }

            account.WriteToEntry();
            if (account.IsIgnored)
            {
                QueueFriendCacheDelete(account.ProviderKey, account.ExternalUserId);
            }

            ApplyExophasePlatformConflicts();
            SchedulePersistAndNotify(account.ProviderKey);
        }

        private bool CanMergeSelectedFriends()
        {
            if (IsBusy)
            {
                return false;
            }

            var accounts = GetSelectedAccountRefs().ToList();
            return accounts.Count >= 2 &&
                   accounts.Select(account => account.ProviderKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == accounts.Count;
        }

        private void MergeSelectedFriends()
        {
            var selectedRows = Friends.Where(row => row.IsSelected).ToList();
            var accounts = GetSelectedAccountRefs().ToList();
            if (accounts.Count < 2)
            {
                return;
            }

            var nickname = selectedRows
                .Select(row => row.Nickname)
                .Concat(selectedRows.Select(row => row.DisplayName))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var avatar = selectedRows
                .Select(row => row.SelectedAvatarAccount)
                .FirstOrDefault(account => account != null) ?? accounts.FirstOrDefault();

            var group = _settings.Persisted?.AddOrUpdateFriendMergeGroup(accounts, nickname, avatar);
            if (group == null)
            {
                StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsSettings_MergeInvalid");
                return;
            }

            ApplyExophasePlatformConflicts();
            RebuildFriends();
            var mergedRow = Friends.FirstOrDefault(row => string.Equals(row.MergeGroupId, group.Id, StringComparison.OrdinalIgnoreCase));
            if (mergedRow != null)
            {
                mergedRow.IsSelected = true;
            }

            PersistAndNotify(null);
        }

        private IEnumerable<FriendAccountRef> GetSelectedAccountRefs()
        {
            return Friends
                .Where(row => row.IsSelected)
                .SelectMany(row => row.GetAccountRefs())
                .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(account => !string.IsNullOrWhiteSpace(account?.Key));
        }

        private void UnmergeFriend(object parameter)
        {
            if (!(parameter is FriendSettingsPersonRowItem row) || !row.IsMerged)
            {
                return;
            }

            if (_settings.Persisted?.RemoveFriendMergeGroup(row.MergeGroupId) == true)
            {
                RebuildFriends();
                PersistAndNotify(null);
            }
        }

        private async Task RefreshAutoDiscoverAsync()
        {
            var providerKeys = AutoDiscoverProviders
                .Where(item => item.IsSelected && item.IsAvailable)
                .Select(item => item.ProviderKey)
                .ToList();
            if (providerKeys.Count == 0)
            {
                StatusText = ResourceProvider.GetString("LOCPlayAch_FriendsSettings_NoAutoProviders");
                return;
            }

            await RefreshRosterAsync(providerKeys, refreshStatus: true).ConfigureAwait(true);
        }

        private async Task AddManualExophaseFriendAsync()
        {
            var username = ExophaseSettings.NormalizeUsername(ManualExophaseUsername);
            if (string.IsNullOrWhiteSpace(username))
            {
                return;
            }

            _settings.Persisted?.AddOrUpdateFriend(
                ExophaseProviderKey,
                username,
                username,
                null,
                null,
                FriendSettingsSource.Manual,
                Enumerable.Empty<string>());
            ManualExophaseUsername = string.Empty;
            PersistAndNotify(ExophaseProviderKey);

            await RefreshRosterAsync(new[] { ExophaseProviderKey }, refreshStatus: false).ConfigureAwait(true);
        }

        private async Task RefreshRosterAsync(IReadOnlyCollection<string> providerKeys, bool refreshStatus)
        {
            if (_plugin?.RefreshRuntime == null)
            {
                return;
            }

            try
            {
                var resolvedProviderKeys = providerKeys ?? Array.Empty<string>();
                IsBusy = true;
                StatusText = ResourceProvider.GetString("LOCPlayAch_Status_Starting");
                var saved = await _plugin.RefreshRuntime
                    .RefreshFriendRosterAsync(resolvedProviderKeys, CancellationToken.None)
                    .ConfigureAwait(true);

                foreach (var key in resolvedProviderKeys)
                {
                    FriendSettingsSyncService.MergeCachedFriends(
                        _settings.Persisted,
                        _friendCache,
                        key,
                        string.Equals(key, ExophaseProviderKey, StringComparison.OrdinalIgnoreCase)
                            ? FriendSettingsSource.Manual
                            : FriendSettingsSource.AutoDiscovered);
                }

                FriendSettingsSyncService.SyncConfiguredFriendsToCache(_settings.Persisted, _friendCache, _logger);
                ApplyExophasePlatformConflicts();
                RebuildFriends();
                PersistAndNotify(null);
                StatusText = refreshStatus
                    ? string.Format(
                        ResourceProvider.GetString("LOCPlayAch_FriendsSettings_RefreshCompleteFormat"),
                        Math.Max(0, saved))
                    : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to refresh friend roster from Friends settings.");
                StatusText = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RemoveFriend(object parameter)
        {
            if (!(parameter is FriendSettingsAccountItem account) || !account.CanRemove)
            {
                return;
            }

            if (_settings.Persisted?.RemoveFriendSetting(account.ProviderKey, account.ExternalUserId) == true)
            {
                _friendCache?.DeleteFriendData(account.ProviderKey, account.ExternalUserId);
                ApplyExophasePlatformConflicts();
                RebuildFriends();
                PersistAndNotify(account.ProviderKey);
            }
        }

        private void ApplyExophasePlatformConflicts()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            var changed = false;
            foreach (var group in persisted.GetFriendMergeGroups())
            {
                var hasSteam = group.Members?.Any(member =>
                    string.Equals(member?.ProviderKey, SteamProviderKey, StringComparison.OrdinalIgnoreCase)) == true;
                var hasRetroAchievements = group.Members?.Any(member =>
                    string.Equals(member?.ProviderKey, RetroAchievementsProviderKey, StringComparison.OrdinalIgnoreCase)) == true;
                if (!hasSteam && !hasRetroAchievements)
                {
                    continue;
                }

                foreach (var exophaseMember in group.Members.Where(member =>
                    string.Equals(member?.ProviderKey, ExophaseProviderKey, StringComparison.OrdinalIgnoreCase)))
                {
                    var entry = persisted.GetFriendSetting(ExophaseProviderKey, exophaseMember.ExternalUserId);
                    var removed = 0;
                    if (hasSteam)
                    {
                        removed += entry?.SelectedPlatforms?.RemoveAll(token =>
                            string.Equals(token, "steam", StringComparison.OrdinalIgnoreCase)) ?? 0;
                    }

                    if (hasRetroAchievements)
                    {
                        removed += entry?.SelectedPlatforms?.RemoveAll(token =>
                            string.Equals(token, "retro", StringComparison.OrdinalIgnoreCase)) ?? 0;
                    }

                    if (removed > 0)
                    {
                        entry.SelectedPlatforms = FriendSettingsEntry.NormalizePlatformList(entry.SelectedPlatforms);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                persisted.Friends = persisted.Friends;
            }
        }

        private static HashSet<string> ResolveDisabledExophasePlatformTokens(IEnumerable<FriendSettingsEntry> entries)
        {
            var list = entries?.Where(entry => entry != null).ToList() ?? new List<FriendSettingsEntry>();
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (list.Any(entry => string.Equals(entry.ProviderKey, SteamProviderKey, StringComparison.OrdinalIgnoreCase)) &&
                list.Any(entry => string.Equals(entry.ProviderKey, ExophaseProviderKey, StringComparison.OrdinalIgnoreCase)))
            {
                disabled.Add("steam");
            }

            if (list.Any(entry => string.Equals(entry.ProviderKey, RetroAchievementsProviderKey, StringComparison.OrdinalIgnoreCase)) &&
                list.Any(entry => string.Equals(entry.ProviderKey, ExophaseProviderKey, StringComparison.OrdinalIgnoreCase)))
            {
                disabled.Add("retro");
            }

            return disabled;
        }

        private void SchedulePersistAndNotify(string providerKey)
        {
            if (!_hasPendingPersist)
            {
                _pendingPersistProviderKey = providerKey;
                _hasPendingPersist = true;
            }
            else if (string.IsNullOrWhiteSpace(_pendingPersistProviderKey) ||
                     string.IsNullOrWhiteSpace(providerKey) ||
                     !string.Equals(_pendingPersistProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            {
                _pendingPersistProviderKey = null;
            }

            _persistDebounceTimer.Stop();
            _persistDebounceTimer.Start();
        }

        private void OnPersistDebounceTimerTick(object sender, EventArgs e)
        {
            _persistDebounceTimer.Stop();
            FlushPendingPersistAndNotify();
        }

        private void FlushPendingPersistAndNotify()
        {
            if (!_hasPendingPersist)
            {
                return;
            }

            var providerKey = _pendingPersistProviderKey;
            _pendingPersistProviderKey = null;
            _hasPendingPersist = false;
            PersistAndNotifyCore(providerKey);
        }

        private void PersistAndNotify(string providerKey)
        {
            _persistDebounceTimer.Stop();
            _pendingPersistProviderKey = null;
            _hasPendingPersist = false;
            PersistAndNotifyCore(providerKey);
        }

        private void PersistAndNotifyCore(string providerKey)
        {
            SyncExophaseProviderFriends(providerKey);
            FriendSettingsSyncService.SyncConfiguredFriendsToCache(_settings.Persisted, _friendCache, _logger, providerKey);
            try
            {
                _plugin?.PersistSettingsForUi();
                _plugin?.ThemeIntegrationService?.RequestUpdate(null, forceRefresh: true);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist Friends settings changes.");
            }
        }

        private void QueueFriendCacheDelete(string providerKey, string externalUserId)
        {
            if (_friendCache == null ||
                string.IsNullOrWhiteSpace(providerKey) ||
                string.IsNullOrWhiteSpace(externalUserId))
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    _friendCache.DeleteFriendData(providerKey, externalUserId);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to delete ignored friend data for {providerKey}/{externalUserId}.");
                }
            });
        }

        private void SyncExophaseProviderFriends(string providerKey)
        {
            if (!string.IsNullOrWhiteSpace(providerKey) &&
                !string.Equals(providerKey, ExophaseProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            try
            {
                var exophaseSettings = _providerRegistry?.GetSettings<ExophaseSettings>() ??
                                       ProviderRegistry.Settings<ExophaseSettings>();
                if (exophaseSettings == null)
                {
                    return;
                }

                var existingByUser = (exophaseSettings.Friends ?? new List<ExophaseFriendSettings>())
                    .Where(friend => !string.IsNullOrWhiteSpace(friend?.Username))
                    .GroupBy(friend => ExophaseSettings.NormalizeUsername(friend.Username), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                var friends = persisted
                    .GetFriendSettings(ExophaseProviderKey, includeIgnored: false)
                    .Where(friend => !string.IsNullOrWhiteSpace(friend?.ExternalUserId))
                    .Select(friend =>
                    {
                        existingByUser.TryGetValue(friend.ExternalUserId, out var existing);
                        return new ExophaseFriendSettings
                        {
                            Username = friend.ExternalUserId,
                            DisplayName = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.ExternalUserId : friend.DisplayName,
                            AvatarUrl = friend.AvatarUrl ?? existing?.AvatarUrl,
                            AvatarPath = friend.AvatarPath ?? existing?.AvatarPath,
                            SelectedPlatforms = friend.SelectedPlatforms?.ToList() ?? new List<string>(),
                            AddedUtc = friend.AddedUtc == default(DateTime)
                                ? (existing?.AddedUtc == default(DateTime) ? DateTime.UtcNow : existing?.AddedUtc ?? DateTime.UtcNow)
                                : friend.AddedUtc,
                            LastRefreshedUtc = friend.LastRefreshedUtc ?? existing?.LastRefreshedUtc,
                            LastProbedUtc = friend.LastProbedUtc ?? existing?.LastProbedUtc,
                            LastProbeStatus = friend.LastProbeStatus ?? existing?.LastProbeStatus,
                            LastError = friend.LastError ?? existing?.LastError
                        };
                    })
                    .ToList();

                exophaseSettings.Friends = friends;
                _providerRegistry?.Save(exophaseSettings, persistToDisk: false);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to mirror Friends settings into Exophase provider settings.");
            }
        }
    }

    internal sealed class FriendAutoDiscoverProviderItem : ObservableObject
    {
        private readonly Action<FriendAutoDiscoverProviderItem> _onChanged;
        private bool _isSelected;

        public FriendAutoDiscoverProviderItem(
            string providerKey,
            string displayName,
            bool isAvailable,
            bool isSelected,
            Action<FriendAutoDiscoverProviderItem> onChanged)
        {
            ProviderKey = providerKey;
            DisplayName = displayName;
            IsAvailable = isAvailable;
            _isSelected = isSelected;
            _onChanged = onChanged;
        }

        public string ProviderKey { get; }

        public string DisplayName { get; }

        public bool IsAvailable { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetValueAndReturn(ref _isSelected, value))
                {
                    _onChanged?.Invoke(this);
                }
            }
        }
    }

    internal sealed class FriendSettingsPersonRowItem : ObservableObject
    {
        private readonly Action<FriendSettingsPersonRowItem> _onChanged;
        private readonly Action<FriendSettingsPersonRowItem> _onSelectionChanged;
        private bool _isSelected;
        private string _nickname;

        public FriendSettingsPersonRowItem(
            FriendMergeGroup group,
            IEnumerable<FriendSettingsEntry> entries,
            HashSet<string> disabledExophasePlatformTokens,
            Action<FriendSettingsPersonRowItem> onChanged,
            Action<FriendSettingsAccountItem> onAccountChanged,
            Action<FriendSettingsPersonRowItem> onSelectionChanged)
        {
            MergeGroupId = group?.Id;
            _nickname = group?.Nickname;
            _onChanged = onChanged;
            _onSelectionChanged = onSelectionChanged;
            Accounts = new ObservableCollection<FriendSettingsAccountItem>(
                (entries ?? Enumerable.Empty<FriendSettingsEntry>())
                .Where(entry => entry != null)
                .Select(entry => new FriendSettingsAccountItem(
                    entry,
                    disabledExophasePlatformTokens,
                    account =>
                    {
                        RefreshDerivedProperties();
                        onAccountChanged?.Invoke(account);
                    },
                    SelectAvatarSource)));
            SeedAvatarSource(group?.AvatarAccount);
            if (!IsMerged && Accounts.Count == 1)
            {
                _nickname = Accounts[0].Entry.Nickname;
            }

            RefreshDerivedProperties();
        }

        public string MergeGroupId { get; }

        public bool IsMerged => !string.IsNullOrWhiteSpace(MergeGroupId);

        public bool CanUnmerge => IsMerged;

        public ObservableCollection<FriendSettingsAccountItem> Accounts { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetValueAndReturn(ref _isSelected, value))
                {
                    _onSelectionChanged?.Invoke(this);
                }
            }
        }

        public string Nickname
        {
            get => _nickname;
            set
            {
                if (SetValueAndReturn(ref _nickname, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    _onChanged?.Invoke(this);
                }
            }
        }

        public string DisplayName => FirstNonEmpty(
            Nickname,
            DefaultDisplayName);

        // The name shown when no nickname is set: the underlying account's display name (or id).
        // Used as the faint placeholder inside the nickname text box.
        public string DefaultDisplayName => FirstNonEmpty(
            Accounts.Select(account => account.DisplayName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Accounts.Select(account => account.ExternalUserId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));

        public string AvatarSource =>
            Accounts.FirstOrDefault(account => account.IsAvatarSource)?.AvatarSource ??
            Accounts.Select(account => account.AvatarSource).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        public FriendAccountRef SelectedAvatarAccount
        {
            get
            {
                var account = Accounts.FirstOrDefault(item => item.IsAvatarSource) ?? Accounts.FirstOrDefault();
                return account == null ? null : FriendAccountRef.From(account.ProviderKey, account.ExternalUserId);
            }
        }

        public string AccountsText => string.Join(", ", Accounts.Select(account => account.ProviderDisplayName));

        public string SortProviderName => IsMerged
            ? ResourceProvider.GetString("LOCPlayAch_FriendsSettings_Merged")
            : Accounts.FirstOrDefault()?.ProviderDisplayName;

        public bool IsIgnored => Accounts.Count > 0 && Accounts.All(account => account.IsIgnored);

        public IEnumerable<FriendAccountRef> GetAccountRefs()
        {
            return Accounts.Select(account => FriendAccountRef.From(account.ProviderKey, account.ExternalUserId));
        }

        public void WritePersonSettings(PersistedSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (IsMerged)
            {
                settings.SetFriendMergeGroupNickname(MergeGroupId, Nickname);
                settings.SetFriendMergeGroupAvatar(MergeGroupId, SelectedAvatarAccount);
            }
            else
            {
                var account = Accounts.FirstOrDefault();
                if (account != null)
                {
                    account.Entry.Nickname = string.IsNullOrWhiteSpace(Nickname) ? null : Nickname.Trim();
                    settings.Friends = settings.Friends;
                }
            }
        }

        public void RefreshPlatformConflicts(HashSet<string> disabledExophasePlatformTokens)
        {
            foreach (var account in Accounts)
            {
                account.SetDisabledExophasePlatformTokens(disabledExophasePlatformTokens);
            }
        }

        private void RefreshDerivedProperties()
        {
            if (!IsMerged && Accounts.Count == 1)
            {
                _nickname = Accounts[0].Entry.Nickname;
            }

            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(AvatarSource));
            OnPropertyChanged(nameof(AccountsText));
            OnPropertyChanged(nameof(IsIgnored));
        }

        // Marks one account as the avatar source without firing the change/persist callback
        // (used once during construction to reflect the stored group avatar).
        private void SeedAvatarSource(FriendAccountRef avatarAccount)
        {
            var chosen = avatarAccount != null
                ? Accounts.FirstOrDefault(account => account.Matches(avatarAccount.ProviderKey, avatarAccount.ExternalUserId))
                : null;
            chosen = chosen
                     ?? Accounts.FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.AvatarSource))
                     ?? Accounts.FirstOrDefault();

            foreach (var account in Accounts)
            {
                account.SetAvatarSourceSelected(ReferenceEquals(account, chosen));
            }
        }

        // Radio-style handler: making one account the avatar source clears the rest, then
        // refreshes the row avatar and persists the choice.
        private void SelectAvatarSource(FriendSettingsAccountItem chosen)
        {
            foreach (var account in Accounts)
            {
                if (!ReferenceEquals(account, chosen))
                {
                    account.SetAvatarSourceSelected(false);
                }
            }

            OnPropertyChanged(nameof(AvatarSource));
            _onChanged?.Invoke(this);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
    }

    internal sealed class FriendSettingsAccountItem : ObservableObject
    {
        private readonly Action<FriendSettingsAccountItem> _onChanged;
        private readonly Action<FriendSettingsAccountItem> _onAvatarSourceSelected;
        private bool _isIgnored;
        private bool _isAvatarSource;

        public FriendSettingsAccountItem(
            FriendSettingsEntry entry,
            HashSet<string> disabledExophasePlatformTokens,
            Action<FriendSettingsAccountItem> onChanged,
            Action<FriendSettingsAccountItem> onAvatarSourceSelected = null)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _onChanged = onChanged;
            _onAvatarSourceSelected = onAvatarSourceSelected;
            ProviderKey = entry.ProviderKey;
            ExternalUserId = entry.ExternalUserId;
            DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.ExternalUserId : entry.DisplayName;
            AvatarSource = !string.IsNullOrWhiteSpace(entry.AvatarPath) ? entry.AvatarPath : entry.AvatarUrl;
            Source = entry.Source;
            _isIgnored = entry.IsIgnored;

            var selected = new HashSet<string>(entry.SelectedPlatforms ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            Platforms = new ObservableCollection<FriendSettingsPlatformToggle>(
                ExophaseFriendPlatformCatalog.Entries.Select(platform => new FriendSettingsPlatformToggle(
                    platform.Token,
                    ResourceProvider.GetString(platform.LabelKey),
                    selected.Contains(platform.Token),
                    OnPlatformChanged)));
            SetDisabledExophasePlatformTokens(disabledExophasePlatformTokens);
        }

        public FriendSettingsEntry Entry { get; }

        public string ProviderKey { get; }

        public string ExternalUserId { get; }

        public string DisplayName { get; }

        public string AvatarSource { get; }

        public FriendSettingsSource Source { get; }

        public bool IsExophase => string.Equals(ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase);

        public bool SupportsPlatformSelection => IsExophase;

        public bool CanRemove => Source == FriendSettingsSource.Manual;

        public string ProviderDisplayName => ProviderRegistry.GetLocalizedName(ProviderKey);

        // Radio-style avatar-source flag. Selecting an account (true) notifies the owning row so
        // it can clear the flag on siblings; clearing (false) is silent to avoid a notify loop.
        public bool IsAvatarSource
        {
            get => _isAvatarSource;
            set
            {
                if (SetValueAndReturn(ref _isAvatarSource, value) && value)
                {
                    _onAvatarSourceSelected?.Invoke(this);
                }
            }
        }

        public void SetAvatarSourceSelected(bool selected)
        {
            SetValueAndReturn(ref _isAvatarSource, selected);
            OnPropertyChanged(nameof(IsAvatarSource));
        }

        public bool Matches(string providerKey, string externalUserId) =>
            string.Equals(
                FriendAccountRef.BuildKey(ProviderKey, ExternalUserId),
                FriendAccountRef.BuildKey(providerKey, externalUserId),
                StringComparison.OrdinalIgnoreCase);

        public string PlatformText => IsExophase
            ? PlatformButtonLabel
            : ProviderDisplayName;

        public ObservableCollection<FriendSettingsPlatformToggle> Platforms { get; }

        public bool IsIgnored
        {
            get => _isIgnored;
            set
            {
                if (SetValueAndReturn(ref _isIgnored, value))
                {
                    _onChanged?.Invoke(this);
                }
            }
        }

        public int SelectedPlatformCount => Platforms.Count(platform => platform.IsSelected);

        public string PlatformButtonLabel => SelectedPlatformCount == 0
            ? ResourceProvider.GetString("LOCPlayAch_Exophase_SelectPlatforms")
            : string.Format(
                ResourceProvider.GetString("LOCPlayAch_Common_SelectedCountFormat"),
                SelectedPlatformCount);

        public void SetDisabledExophasePlatformTokens(HashSet<string> disabledTokens)
        {
            var changed = false;
            foreach (var platform in Platforms)
            {
                var shouldDisable = IsExophase &&
                                    disabledTokens?.Contains(platform.Token) == true;
                if (platform.IsEnabled == !shouldDisable)
                {
                    continue;
                }

                platform.SetEnabled(!shouldDisable);
                if (shouldDisable && platform.IsSelected)
                {
                    platform.SetSelected(false, notify: false);
                    changed = true;
                }
            }

            if (changed)
            {
                OnPlatformChanged();
            }
        }

        public void WriteToEntry()
        {
            Entry.IsIgnored = IsIgnored;
            Entry.SelectedPlatforms = Platforms
                .Where(platform => platform.IsEnabled && platform.IsSelected)
                .Select(platform => platform.Token)
                .ToList();
        }

        private void OnPlatformChanged()
        {
            OnPropertyChanged(nameof(SelectedPlatformCount));
            OnPropertyChanged(nameof(PlatformButtonLabel));
            OnPropertyChanged(nameof(PlatformText));
            _onChanged?.Invoke(this);
        }
    }

    internal sealed class FriendSettingsPlatformToggle : ObservableObject
    {
        private readonly Action _onChanged;
        private bool _isSelected;
        private bool _isEnabled = true;

        public FriendSettingsPlatformToggle(string token, string label, bool isSelected, Action onChanged)
        {
            Token = token;
            Label = label;
            _isSelected = isSelected;
            _onChanged = onChanged;
        }

        public string Token { get; }

        public string Label { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            private set => SetValue(ref _isEnabled, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetSelected(value, notify: true);
        }

        public void SetEnabled(bool value)
        {
            IsEnabled = value;
        }

        public void SetSelected(bool value, bool notify)
        {
            if (SetValueAndReturn(ref _isSelected, value) && notify)
            {
                _onChanged?.Invoke();
            }
        }
    }
}
