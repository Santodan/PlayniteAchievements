using System;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;

namespace PlayniteAchievements.ViewModels
{
    public class RecentAchievementItem : ObservableObject
    {
        private const string DefaultIconPackUri = "pack://application:,,,/PlayniteAchievements;component/Resources/HiddenAchIcon.png";

        public string ApiName { get; set; }
        public Guid? PlayniteGameId { get; set; }

        private string _providerKey;
        public string ProviderKey
        {
            get => _providerKey;
            set => SetValue(ref _providerKey, value);
        }

        private bool _hidden;
        public bool Hidden
        {
            get => _hidden;
            set
            {
                if (SetValueAndReturn(ref _hidden, value))
                {
                    OnPropertyChanged(nameof(HiddenTitleSuffix));
                }
            }
        }

        private bool _showHiddenSuffix = true;
        public bool ShowHiddenSuffix
        {
            get => _showHiddenSuffix;
            set
            {
                if (SetValueAndReturn(ref _showHiddenSuffix, value))
                {
                    OnPropertyChanged(nameof(HiddenTitleSuffix));
                }
            }
        }

        private string _name;
        public string Name 
        { 
            get => _name; 
            set => SetValue(ref _name, value); 
        }

        private string _description;
        public string Description 
        { 
            get => _description; 
            set => SetValue(ref _description, value); 
        }

        private string _gameName;
        public string GameName 
        { 
            get => _gameName; 
            set => SetValue(ref _gameName, value); 
        }

        private string _iconPath;
        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (SetValueAndReturn(ref _iconPath, value))
                {
                    OnPropertyChanged(nameof(DisplayIcon));
                }
            }
        }

        public string DisplayIcon
        {
            get
            {
                if (string.IsNullOrWhiteSpace(IconPath))
                {
                    return DefaultIconPackUri;
                }

                return IconPath;
            }
        }

        /// <summary>
        /// Recent achievements are always unlocked by definition.
        /// </summary>
        public bool Unlocked => true;

        private DateTime _unlockTime;
        public DateTime UnlockTime 
        { 
            get => _unlockTime; 
            set 
            {
                // Use explicit check because SetValue is void
                if (_unlockTime != value)
                {
                    SetValue(ref _unlockTime, value);
                    OnPropertyChanged(nameof(UnlockTimeText));
                }
            } 
        }

        private double? _globalPercentUnlocked;
        /// <summary>
        /// Global unlock percentage. Null if rarity data is not available for this provider.
        /// </summary>
        public double? GlobalPercentUnlocked
        {
            get => _globalPercentUnlocked;
            set
            {
                if (SetValueAndReturn(ref _globalPercentUnlocked, value))
                {
                    OnPropertyChanged(nameof(GlobalPercent));
                    OnPropertyChanged(nameof(GlobalPercentText));
                    OnPropertyChanged(nameof(HasRarityPercent));
                    OnPropertyChanged(nameof(RaritySortValue));
                }
            }
        }

        /// <summary>
        /// Non-nullable version for backwards compatibility. Returns 0 if null.
        /// </summary>
        public double GlobalPercent => GlobalPercentUnlocked ?? 0;

        public double RarityPercentValue => GlobalPercentUnlocked ?? 0;

        private RarityTier _rarity = RarityTier.Common;
        public RarityTier Rarity
        {
            get => _rarity;
            set
            {
                if (SetValueAndReturn(ref _rarity, value))
                {
                    OnPropertyChanged(nameof(GlobalPercentText));
                    OnPropertyChanged(nameof(RarityBrush));
                    OnPropertyChanged(nameof(RaritySortValue));
                }
            }
        }

        private int? _pointsValue;
        public int? PointsValue
        {
            get => _pointsValue;
            set
            {
                if (SetValueAndReturn(ref _pointsValue, value))
                {
                    OnPropertyChanged(nameof(Points));
                    OnPropertyChanged(nameof(PointsText));
                }
            }
        }

        private int? _progressNum;
        public int? ProgressNum
        {
            get => _progressNum;
            set => SetValue(ref _progressNum, value);
        }

        private int? _progressDenom;
        public int? ProgressDenom
        {
            get => _progressDenom;
            set => SetValue(ref _progressDenom, value);
        }

        private string _gameIconPath;
        public string GameIconPath 
        { 
            get => _gameIconPath; 
            set => SetValue(ref _gameIconPath, value); 
        }

        private string _gameCoverPath;
        public string GameCoverPath 
        { 
            get => _gameCoverPath; 
            set => SetValue(ref _gameCoverPath, value); 
        }

        private string _trophyType;
        /// <summary>
        /// Trophy type for PlayStation games: "bronze", "silver", "gold", "platinum".
        /// Null for non-PlayStation achievements.
        /// </summary>
        public string TrophyType
        {
            get => _trophyType;
            set
            {
                if (SetValueAndReturn(ref _trophyType, value))
                {
                    OnPropertyChanged(nameof(HasTrophyType));
                }
            }
        }

        private string _categoryType;
        public string CategoryType
        {
            get => _categoryType;
            set
            {
                if (SetValueAndReturn(ref _categoryType, value))
                {
                    OnPropertyChanged(nameof(CategoryTypeDisplay));
                }
            }
        }

        public string CategoryTypeDisplay => AchievementCategoryTypeHelper.ToDisplayText(CategoryType);

        private string _categoryLabel;
        public string CategoryLabel
        {
            get => _categoryLabel;
            set => SetValue(ref _categoryLabel, value);
        }

        /// <summary>
        /// True if this achievement has PlayStation trophy type data.
        /// </summary>
        public bool HasTrophyType => !string.IsNullOrWhiteSpace(TrophyType);

        public string UnlockTimeText => UnlockTime.ToLocalTime().ToString("g");
        public string GlobalPercentText => AchievementRarityResolver.GetDisplayText(GlobalPercentUnlocked, Rarity);
        public int Points => PointsValue ?? 0;
        public string PointsText => PointsValue.HasValue ? PointsValue.Value.ToString() : "-";

        /// <summary>
        /// True if rarity percentage data is available for this achievement.
        /// </summary>
        public bool HasRarityPercent => GlobalPercentUnlocked.HasValue;

        public double RaritySortValue => AchievementRarityResolver.GetSortValue(GlobalPercentUnlocked, Rarity);

        public System.Windows.Media.SolidColorBrush RarityBrush => Rarity.ToBrush();
        public string HiddenTitleSuffix => ShowHiddenSuffix && Hidden ? ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle_WithParens") : string.Empty;

        public void UpdateFrom(RecentAchievementItem other)
        {
            if (other == null) return;

            // ApiName is immutable/key
            if (ApiName != other.ApiName) ApiName = other.ApiName;
            PlayniteGameId = other.PlayniteGameId;
            ProviderKey = other.ProviderKey;

            Name = other.Name;
            Description = other.Description;
            GameName = other.GameName;
            IconPath = other.IconPath;
            UnlockTime = other.UnlockTime;
            GlobalPercentUnlocked = other.GlobalPercentUnlocked;
            Rarity = other.Rarity;
            PointsValue = other.PointsValue;
            ProgressNum = other.ProgressNum;
            ProgressDenom = other.ProgressDenom;
            GameIconPath = other.GameIconPath;
            GameCoverPath = other.GameCoverPath;
            TrophyType = other.TrophyType;
            CategoryType = other.CategoryType;
            CategoryLabel = other.CategoryLabel;
        }
    }
}
