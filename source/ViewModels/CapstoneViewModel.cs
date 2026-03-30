using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public sealed class CapstoneViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private List<CapstoneOptionItem> _allOptions = new List<CapstoneOptionItem>();
        private string _searchText = string.Empty;

        public event EventHandler CapstoneChanged;

        public CapstoneViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;

            ClearSearchCommand = new RelayCommand(_ => ClearSearch());

            LoadData();
        }

        public ObservableCollection<CapstoneOptionItem> AchievementOptions { get; } =
            new ObservableCollection<CapstoneOptionItem>();

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

        public void SetMarker(CapstoneOptionItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ApiName))
            {
                return;
            }

            var result = _achievementOverridesService.SetCapstone(_gameId, item.ApiName);
            if (!result.Success)
            {
                ShowError(ResolveErrorMessage(result));
                return;
            }

            UpdateMarkerSelection(item);
            CapstoneChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ClearMarker()
        {
            var result = _achievementOverridesService.SetCapstone(_gameId, null);
            if (!result.Success)
            {
                ShowError(ResolveErrorMessage(result));
                return;
            }

            UpdateMarkerSelection(null);
            CapstoneChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleReveal(CapstoneOptionItem item)
        {
            if (item != null && item.CanReveal)
            {
                item.ToggleReveal();
            }
        }

        private void UpdateMarkerSelection(CapstoneOptionItem newMarker)
        {
            var markerApiName = NormalizeText(newMarker?.ApiName);
            CapstoneOptionItem matchedMarker = null;

            foreach (var option in AchievementOptions)
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

            SetCurrentMarkerText(matchedMarker?.DisplayName ?? newMarker?.DisplayName);
        }

        private void ApplyFilter()
        {
            var filtered = _allOptions.AsEnumerable();

            if (!string.IsNullOrEmpty(SearchText))
            {
                filtered = filtered.Where(item =>
                    (item.DisplayName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (item.Description?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            AchievementOptions.Clear();
            foreach (var item in filtered)
            {
                AchievementOptions.Add(item);
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
                GameName = game?.Name ?? ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame") ?? "Unknown Game";

                UseCoverAspect = _settings?.Persisted?.UseCoverImages ?? false;

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

                var gameData = _achievementDataService.GetGameAchievementData(_gameId);
                var achievements = gameData?.Achievements ?? new List<AchievementDetail>();
                var projectionOptions = AchievementProjectionService.CreateOptions(_settings, gameData);

                // Find the current capstone by checking IsCapstone on achievements
                var currentCapstone = achievements.FirstOrDefault(a => a?.IsCapstone == true);
                var currentCapstoneApiName = NormalizeText(currentCapstone?.ApiName);

                var sortedAchievements = achievements
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .OrderBy(a => a.RaritySortValue)  // Rarest first
                    .ThenByDescending(a => a.Points ?? 0)
                    .ThenBy(a => a.DisplayName ?? a.ApiName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                _allOptions.Clear();
                CapstoneOptionItem currentMarkerOption = null;
                for (int i = 0; i < sortedAchievements.Count; i++)
                {
                    var achievement = sortedAchievements[i];
                    var projected = AchievementProjectionService.CreateDisplayItem(
                        gameData,
                        achievement,
                        projectionOptions,
                        _gameId);
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

                ApplyFilter();
                SetCurrentMarkerText(currentMarkerOption?.DisplayName);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading capstone options for gameId={_gameId}");
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
                IconPath = projected.IconPath,
                UnlockTimeUtc = projected.UnlockTimeUtc,
                GlobalPercentUnlocked = projected.GlobalPercentUnlocked,
                PointsValue = projected.PointsValue,
                ProgressNum = projected.ProgressNum,
                ProgressDenom = projected.ProgressDenom,
                TrophyType = projected.TrophyType,
                Unlocked = projected.Unlocked,
                Hidden = projected.Hidden,
                ShowHiddenIcon = projected.ShowHiddenIcon,
                ShowHiddenTitle = projected.ShowHiddenTitle,
                ShowHiddenDescription = projected.ShowHiddenDescription,
                ShowRarityGlow = projected.ShowRarityGlow,
                ShowRarityBar = projected.ShowRarityBar,
                ShowHiddenSuffix = projected.ShowHiddenSuffix,
                ShowLockedIcon = projected.ShowLockedIcon,
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
            var defaultMessage = ResourceProvider.GetString("LOCPlayAch_Capstone_Error_SaveFailed");
            if (result == null)
            {
                return defaultMessage;
            }

            switch ((result.ErrorCode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "invalid_game_id":
                    return ResourceProvider.GetString("LOCPlayAch_Capstone_Error_InvalidGame") ?? defaultMessage;
                case "game_not_cached":
                    return ResourceProvider.GetString("LOCPlayAch_Capstone_Error_NotCached") ?? defaultMessage;
                case "marker_not_found":
                    return ResourceProvider.GetString("LOCPlayAch_Capstone_Error_MarkerNotFound") ?? defaultMessage;
                default:
                    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        return result.ErrorMessage;
                    }

                    return defaultMessage;
            }
        }

        private void ShowError(string message)
        {
            _playniteApi?.Dialogs?.ShowMessage(
                message ?? ResourceProvider.GetString("LOCPlayAch_Capstone_Error_SaveFailed"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private void SetCurrentMarkerText(string markerDisplayName)
        {
            var fallback = ResourceProvider.GetString("LOCPlayAch_Capstone_Current_None") ?? "None";
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

        public bool IsCapstone { get; set; }
    }
}



