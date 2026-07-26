using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public sealed class CapstoneChangedEventArgs : EventArgs
    {
        public CapstoneChangedEventArgs(string apiName, string displayName)
        {
            ApiName = apiName;
            DisplayName = displayName;
        }

        public string ApiName { get; }

        public string DisplayName { get; }
    }

    public sealed class CapstoneViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private List<CapstoneOptionItem> _allOptions = new List<CapstoneOptionItem>();
        private readonly SearchTextIndex<CapstoneOptionItem> _searchIndex =
            new SearchTextIndex<CapstoneOptionItem>(item =>
                SearchTextBuilder.ForCapstone(item?.DisplayName, item?.Description));
        private string _searchText = string.Empty;
        private string _persistedMarkerApiName;
        private int _capstoneWriteVersion;

        public event EventHandler<CapstoneChangedEventArgs> CapstoneChanged;

        public CapstoneViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ?? throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;

            ClearSearchCommand = new RelayCommand(_ => ClearSearch());

            LoadData();
        }

        public ObservableCollection<CapstoneOptionItem> AchievementOptions { get; } =
            new PlayniteAchievements.Common.BulkObservableCollection<CapstoneOptionItem>();

        private string _gameName;
        public string GameName
        {
            get => _gameName;
            private set => SetValue(ref _gameName, value);
        }

        private string _gameImagePath;
        public string GameImagePath
        {
            get => _gameImagePath;
            private set => SetValue(ref _gameImagePath, value);
        }

        private bool _useCoverAspect;
        public bool UseCoverAspect
        {
            get => _useCoverAspect;
            private set => SetValue(ref _useCoverAspect, value);
        }

        private string _currentMarkerText;
        public string CurrentMarkerText
        {
            get => _currentMarkerText;
            private set => SetValue(ref _currentMarkerText, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public RelayCommand ClearSearchCommand { get; }

        public async Task SetMarkerAsync(CapstoneOptionItem item)
        {
            var markerApiName = NormalizeText(item?.ApiName);
            if (string.IsNullOrWhiteSpace(markerApiName))
            {
                return;
            }

            var displayName = UpdateMarkerSelection(item);
            var writeVersion = Interlocked.Increment(ref _capstoneWriteVersion);
            var result = await PersistCapstoneAsync(markerApiName);
            if (result.Success)
            {
                _persistedMarkerApiName = markerApiName;
                if (IsLatestCapstoneWrite(writeVersion))
                {
                    CapstoneChanged?.Invoke(this, new CapstoneChangedEventArgs(markerApiName, displayName));
                }
                return;
            }

            if (IsLatestCapstoneWrite(writeVersion))
            {
                UpdateMarkerSelection(FindOptionByApiName(_persistedMarkerApiName));
                ShowError(ResolveErrorMessage(result));
            }
        }

        public async Task ClearMarkerAsync()
        {
            UpdateMarkerSelection(null);
            var writeVersion = Interlocked.Increment(ref _capstoneWriteVersion);
            var result = await PersistCapstoneAsync(null);
            if (result.Success)
            {
                _persistedMarkerApiName = null;
                if (IsLatestCapstoneWrite(writeVersion))
                {
                    CapstoneChanged?.Invoke(this, new CapstoneChangedEventArgs(null, null));
                }
                return;
            }

            if (IsLatestCapstoneWrite(writeVersion))
            {
                UpdateMarkerSelection(FindOptionByApiName(_persistedMarkerApiName));
                ShowError(ResolveErrorMessage(result));
            }
        }

        public void ToggleReveal(CapstoneOptionItem item)
        {
            if (item != null && item.CanReveal)
            {
                item.ToggleReveal();
                _searchIndex.Invalidate(item);
            }
        }

        private string UpdateMarkerSelection(CapstoneOptionItem newMarker)
        {
            var markerApiName = NormalizeText(newMarker?.ApiName);
            CapstoneOptionItem matchedMarker = null;

            foreach (var option in _allOptions)
            {
                var isMatch = !string.IsNullOrWhiteSpace(markerApiName) &&
                              string.Equals(
                                  NormalizeText(option?.ApiName),
                                  markerApiName,
                                  StringComparison.OrdinalIgnoreCase);
                option.IsCurrentMarker = isMatch;
                if (isMatch)
                {
                    matchedMarker = option;
                }
            }

            var displayName = matchedMarker?.DisplayName ?? newMarker?.DisplayName;
            SetCurrentMarkerText(displayName);
            return displayName;
        }

        private CapstoneOptionItem FindOptionByApiName(string apiName)
        {
            var normalized = NormalizeText(apiName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return _allOptions.FirstOrDefault(option =>
                string.Equals(
                    NormalizeText(option?.ApiName),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }

        private bool IsLatestCapstoneWrite(int writeVersion)
        {
            return writeVersion == Volatile.Read(ref _capstoneWriteVersion);
        }

        private async Task<CacheWriteResult> PersistCapstoneAsync(string markerApiName)
        {
            try
            {
                return await _achievementOverridesService
                    .SetCapstoneAsync(_gameId, markerApiName)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed setting capstone for gameId={_gameId}.");
                return CacheWriteResult.CreateFailure(
                    _gameId.ToString("D"),
                    "settings_save_failed",
                    ex.Message,
                    ex);
            }
        }

        private void ApplyFilter()
        {
            var filtered = _allOptions.AsEnumerable();
            var searchQuery = SearchQuery.From(SearchText);

            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(item => _searchIndex.Matches(item, searchQuery));
            }

            ReplaceAchievementOptions(filtered.ToList());
        }

        private void ReplaceAchievementOptions(IEnumerable<CapstoneOptionItem> options)
        {
            if (AchievementOptions is PlayniteAchievements.Common.BulkObservableCollection<CapstoneOptionItem> bulk)
            {
                bulk.ReplaceAll(options);
            }
            else
            {
                PlayniteAchievements.Common.CollectionHelper.SynchronizeCollection(AchievementOptions, options);
            }
        }

        public void ClearSearch()
        {
            SearchText = string.Empty;
        }

        public void ReloadData()
        {
            LoadData();
        }

        private void LoadData()
        {
            try
            {
                var revealedStateByApiName = _allOptions
                    .Concat(AchievementOptions)
                    .Where(option => option != null && !string.IsNullOrWhiteSpace(option.ApiName))
                    .GroupBy(option => option.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);

                var game = _playniteApi?.Database?.Games?.Get(_gameId);
                GameName = game?.Name ?? ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame");

                UseCoverAspect = _settings?.Persisted?.OverviewGameSummariesUseCoverImages ?? true;

                if (game != null)
                {
                    if (UseCoverAspect && !string.IsNullOrEmpty(game.CoverImage))
                    {
                        GameImagePath = _playniteApi?.Database?.GetFullFilePath(game.CoverImage);
                    }
                    else if (!string.IsNullOrEmpty(game.Icon))
                    {
                        GameImagePath = _playniteApi?.Database?.GetFullFilePath(game.Icon);
                    }
                }

                var gameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var achievements = gameData?.Achievements ?? new List<AchievementDetail>();
                // Find the current capstone by checking IsCapstone on achievements
                var currentCapstone = achievements.FirstOrDefault(a => a?.IsCapstone == true);
                var currentCapstoneApiName = NormalizeText(currentCapstone?.ApiName);

                var sortedAchievements = achievements
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .OrderBy(a => a.RaritySortValue)  // Rarest first
                    .ThenByDescending(a => a.Points ?? 0)
                    .ThenBy(a => a.DisplayName ?? a.ApiName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                // Per-game invariants hoisted out of the row loop: the appearance snapshot and
                // category art/order resolution are identical for every row in this pass.
                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    gameData?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                _allOptions.Clear();
                CapstoneOptionItem currentMarkerOption = null;
                for (int i = 0; i < sortedAchievements.Count; i++)
                {
                    var achievement = sortedAchievements[i];
                    var projected = AchievementDisplayItem.Create(
                        gameData,
                        achievement,
                        _settings,
                        playniteGameIdOverride: _gameId,
                        appearanceSettings: appearanceSnapshot,
                        categoryMemo: categoryMemo);
                    var option = CreateOptionItem(projected, achievement, currentCapstoneApiName);
                    if (option == null)
                    {
                        continue;
                    }

                    if (option.IsCurrentMarker)
                    {
                        currentMarkerOption = option;
                    }

                    var apiName = (option.ApiName ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        revealedStateByApiName.TryGetValue(apiName, out var isRevealed))
                    {
                        option.IsRevealed = isRevealed;
                    }

                    _allOptions.Add(option);
                }

                _searchIndex.Rebuild(_allOptions);
                ApplyFilter();
                _persistedMarkerApiName = currentCapstoneApiName;
                SetCurrentMarkerText(currentMarkerOption?.DisplayName);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading capstone options for gameId={_gameId}");
                _searchIndex.Clear();
                SetCurrentMarkerText(null);
            }
        }

        private CapstoneOptionItem CreateOptionItem(
            AchievementDisplayItem projected,
            AchievementDetail sourceAchievement,
            string currentCapstoneApiName)
        {
            if (projected == null || sourceAchievement == null || string.IsNullOrWhiteSpace(projected.ApiName))
            {
                return null;
            }

            return new CapstoneOptionItem
            {
                ProviderKey = projected.ProviderKey,
                GameName = projected.GameName,
                SortingName = projected.SortingName,
                PlayniteGameId = projected.PlayniteGameId,
                ApiName = projected.ApiName,
                DisplayName = projected.DisplayName,
                Description = projected.Description,
                UnlockedIconPath = projected.UnlockedIconPath,
                LockedIconPath = projected.LockedIconPath,
                UnlockTimeUtc = projected.UnlockTimeUtc,
                GlobalPercentUnlocked = projected.GlobalPercentUnlocked,
                Rarity = projected.Rarity,
                PointsValue = projected.PointsValue,
                ProgressNum = projected.ProgressNum,
                ProgressDenom = projected.ProgressDenom,
                TrophyType = projected.TrophyType,
                Unlocked = projected.Unlocked,
                Hidden = projected.Hidden,
                ShowHiddenIcon = projected.ShowHiddenIcon,
                ShowHiddenTitle = projected.ShowHiddenTitle,
                ShowHiddenDescription = projected.ShowHiddenDescription,
                ShowRarityBar = projected.ShowRarityBar,
                ShowHiddenSuffix = projected.ShowHiddenSuffix,
                ShowLockedIcon = projected.ShowLockedIcon,
                UseSeparateLockedIconsWhenAvailable = projected.UseSeparateLockedIconsWhenAvailable,
                IsRevealed = projected.IsRevealed,
                IsCapstone = sourceAchievement.IsCapstone,
                IsCurrentMarker = string.Equals(
                    projected.ApiName,
                    currentCapstoneApiName,
                    StringComparison.OrdinalIgnoreCase)
            };
        }

        private string ResolveErrorMessage(CacheWriteResult result)
        {
            return BuildGenericErrorMessage(result?.ErrorMessage);
        }

        private void ShowError(string message)
        {
            _playniteApi?.Dialogs?.ShowMessage(
                message ?? BuildGenericErrorMessage(),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }

        private static string BuildGenericErrorMessage(string detail = null)
        {
            var format = ResourceProvider.GetString("LOCPlayAch_Status_Failed");
            if (string.IsNullOrWhiteSpace(format))
            {
                format = "Error: {0}";
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = "Operation failed.";
                }
            }

            try
            {
                return string.Format(format, detail);
            }
            catch (FormatException)
            {
                return string.Concat(format, " ", detail).Trim();
            }
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private void SetCurrentMarkerText(string markerDisplayName)
        {
            var fallback = ResourceProvider.GetString("LOCPlayAch_Common_None");
            var markerText = string.IsNullOrWhiteSpace(markerDisplayName) ? fallback : markerDisplayName.Trim();
            var format = ResourceProvider.GetString("LOCPlayAch_Capstone_Current");
            if (string.IsNullOrWhiteSpace(format))
            {
                format = "Current capstone: {0}";
            }

            CurrentMarkerText = string.Format(format, markerText);
        }
    }

    public sealed class CapstoneOptionItem : AchievementDisplayItem
    {
        private bool _isCurrentMarker;
        public bool IsCurrentMarker
        {
            get => _isCurrentMarker;
            set => SetValue(ref _isCurrentMarker, value);
        }
    }
}




