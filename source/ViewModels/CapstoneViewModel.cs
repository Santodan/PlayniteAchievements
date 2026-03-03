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
        private const string HiddenIconPath = "pack://application:,,,/PlayniteAchievements;component/Resources/HiddenAchIcon.png";

        private readonly Guid _gameId;
        private readonly AchievementService _achievementService;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private List<CapstoneOptionItem> _allOptions = new List<CapstoneOptionItem>();
        private string _searchText = string.Empty;

        public event EventHandler CapstoneChanged;

        public CapstoneViewModel(
            Guid gameId,
            AchievementService achievementService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
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

            var result = _achievementService.SetCapstone(_gameId, item.ApiName);
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
            var result = _achievementService.SetCapstone(_gameId, null);
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

                var gameData = _achievementService.GetGameAchievementData(_gameId);
                var achievements = gameData?.Achievements ?? new List<AchievementDetail>();

                // Find the current capstone by checking IsCapstone on achievements
                var currentCapstone = achievements.FirstOrDefault(a => a?.IsCapstone == true);
                var currentCapstoneApiName = NormalizeText(currentCapstone?.ApiName);

                var sortedAchievements = achievements
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .OrderBy(a => a.GlobalPercentUnlocked ?? 100)  // Rarest first
                    .ThenBy(a => a.DisplayName ?? a.ApiName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                _allOptions.Clear();
                CapstoneOptionItem currentMarkerOption = null;
                for (int i = 0; i < sortedAchievements.Count; i++)
                {
                    var achievement = sortedAchievements[i];
                    var option = CreateOptionItem(achievement, currentCapstoneApiName);

                    if (option.IsCurrentMarker)
                    {
                        currentMarkerOption = option;
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

        private CapstoneOptionItem CreateOptionItem(AchievementDetail achievement, string currentCapstoneApiName)
        {
            var actualIcon = ResolveActualIconPath(achievement);
            var hidden = achievement.Hidden && !achievement.Unlocked;

            return new CapstoneOptionItem
            {
                ApiName = achievement.ApiName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(achievement.DisplayName)
                    ? achievement.ApiName
                    : achievement.DisplayName.Trim(),
                Description = string.IsNullOrWhiteSpace(achievement.Description)
                    ? string.Empty
                    : achievement.Description.Trim(),
                ActualIconPath = actualIcon,
                HiddenIconPath = HiddenIconPath,
                Unlocked = achievement.Unlocked,
                Hidden = hidden,
                IsCapstone = achievement.IsCapstone,
                IsCurrentMarker = string.Equals(
                    achievement.ApiName.Trim(),
                    currentCapstoneApiName,
                    StringComparison.OrdinalIgnoreCase),
                GlobalPercentUnlocked = achievement.GlobalPercentUnlocked,
                PointsValue = achievement.Points
            };
        }

        private static string ResolveActualIconPath(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return HiddenIconPath;
            }

            // Always use UnlockedIconPath (which is cached during refresh)
            // Apply grayscale for locked achievements, same as theme integration
            var icon = achievement.UnlockedIconPath;

            if (string.IsNullOrWhiteSpace(icon))
            {
                return HiddenIconPath;
            }

            if (!achievement.Unlocked)
            {
                return AchievementIconResolver.ApplyGrayPrefix(icon);
            }

            return icon;
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

    public sealed class CapstoneOptionItem : ObservableObject
    {
        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetValueAndReturn(ref _displayName, value))
                {
                    OnPropertyChanged(nameof(DisplayNameResolved));
                }
            }
        }

        private string _description;
        public string Description
        {
            get => _description;
            set
            {
                if (SetValueAndReturn(ref _description, value))
                {
                    OnPropertyChanged(nameof(DescriptionResolved));
                }
            }
        }

        private string _actualIconPath;
        public string ActualIconPath
        {
            get => _actualIconPath;
            set
            {
                if (SetValueAndReturn(ref _actualIconPath, value))
                {
                    OnPropertyChanged(nameof(DisplayIcon));
                }
            }
        }

        private string _hiddenIconPath;
        public string HiddenIconPath
        {
            get => _hiddenIconPath;
            set
            {
                if (SetValueAndReturn(ref _hiddenIconPath, value))
                {
                    OnPropertyChanged(nameof(DisplayIcon));
                }
            }
        }

        private bool _isRevealed;
        public bool IsRevealed
        {
            get => _isRevealed;
            set
            {
                if (SetValueAndReturn(ref _isRevealed, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsEffectivelyHidden));
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(DescriptionResolved));
                }
            }
        }

        private bool _isCurrentMarker;
        public bool IsCurrentMarker
        {
            get => _isCurrentMarker;
            set => SetValue(ref _isCurrentMarker, value);
        }

        public string ApiName { get; set; }
        public bool Unlocked { get; set; }
        public bool Hidden { get; set; }
        public bool IsCapstone { get; set; }

        /// <summary>
        /// True if the achievement can be revealed (hidden and not unlocked).
        /// </summary>
        public bool CanReveal => Hidden && !Unlocked;

        /// <summary>
        /// True if the achievement is hidden and not yet revealed.
        /// </summary>
        public bool IsEffectivelyHidden => CanReveal && !IsRevealed;

        /// <summary>
        /// The icon to display - shows hidden icon if effectively hidden, otherwise actual icon.
        /// </summary>
        public string DisplayIcon => IsEffectivelyHidden ? HiddenIconPath : ActualIconPath;

        /// <summary>
        /// Display name - shows "Hidden Achievement" if effectively hidden.
        /// </summary>
        public string DisplayNameResolved
        {
            get
            {
                if (IsEffectivelyHidden)
                {
                    return ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle") ?? "Hidden Achievement";
                }
                return DisplayName;
            }
        }

        /// <summary>
        /// Description - shows "???" if effectively hidden.
        /// </summary>
        public string DescriptionResolved
        {
            get
            {
                if (IsEffectivelyHidden)
                {
                    return "???";
                }
                return Description;
            }
        }

        public void ToggleReveal()
        {
            if (CanReveal)
            {
                IsRevealed = !IsRevealed;
            }
        }

        // Rarity data
        public double? GlobalPercentUnlocked { get; set; }

        public RarityTier Rarity => RarityHelper.GetRarityTier(GlobalPercentUnlocked ?? 100);

        /// <summary>
        /// True if this achievement has rarity data (GlobalPercentUnlocked is set).
        /// </summary>
        public bool HasRarity => GlobalPercentUnlocked.HasValue;

        public string GlobalPercentText =>
            GlobalPercentUnlocked.HasValue ? $"{GlobalPercentUnlocked.Value:F1}%" : "-";

        public double GlobalPercent => GlobalPercentUnlocked ?? 100;

        // Points data
        public int? PointsValue { get; set; }

        public int Points => PointsValue ?? 0;

        public string PointsText => PointsValue.HasValue ? PointsValue.Value.ToString() : "-";
    }
}



