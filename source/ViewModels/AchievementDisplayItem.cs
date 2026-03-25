using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Display model for achievements in views.
    /// </summary>
    public class AchievementDisplayItem : ObservableObject
    {
        private string _gameName;
        public string GameName { get => _gameName; set => SetValue(ref _gameName, value); }

        private string _sortingName;
        public string SortingName { get => _sortingName; set => SetValue(ref _sortingName, value); }

        private Guid? _playniteGameId;
        public Guid? PlayniteGameId { get => _playniteGameId; set => SetValue(ref _playniteGameId, value); }

        private string _providerKey;
        public string ProviderKey
        {
            get => _providerKey;
            set => SetValue(ref _providerKey, value);
        }

        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (SetValueAndReturn(ref _displayName, value))
                {
                    OnPropertyChanged(nameof(Name));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(HiddenTitleSuffix));
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

        private string _iconPath;
        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (SetValueAndReturn(ref _iconPath, value))
                {
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        private DateTime? _unlockTimeUtc;
        public DateTime? UnlockTimeUtc
        {
            get => _unlockTimeUtc;
            set
            {
                if (SetValueAndReturn(ref _unlockTimeUtc, value))
                {
                    OnPropertyChanged(nameof(UnlockTimeText));
                    OnPropertyChanged(nameof(DateUnlocked));
                }
            }
        }

        private double? _globalPercentUnlocked;
        public double? GlobalPercentUnlocked
        {
            get => _globalPercentUnlocked;
            set
            {
                if (SetValueAndReturn(ref _globalPercentUnlocked, value))
                {
                    OnPropertyChanged(nameof(HasRarityPercent));
                    OnPropertyChanged(nameof(GlobalPercentText));
                    OnPropertyChanged(nameof(RarityDetailText));
                    OnPropertyChanged(nameof(GlobalPercent));
                    OnPropertyChanged(nameof(RarityPercentValue));
                    OnPropertyChanged(nameof(Percent));
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

        private RarityTier _rarity = RarityTier.Common;
        public RarityTier Rarity
        {
            get => _rarity;
            set
            {
                if (SetValueAndReturn(ref _rarity, value))
                {
                    OnPropertyChanged(nameof(GlobalPercentText));
                    OnPropertyChanged(nameof(RarityDetailText));
                    OnPropertyChanged(nameof(GamerScore));
                    OnPropertyChanged(nameof(RaritySortValue));
                }
            }
        }

        private bool _unlocked;
        public bool Unlocked
        {
            get => _unlocked;
            set
            {
                if (SetValueAndReturn(ref _unlocked, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(HiddenTitleSuffix));
                    OnPropertyChanged(nameof(DescriptionResolved));
                    OnPropertyChanged(nameof(IsUnlock));
                }
            }
        }

        private bool _hidden;
        public bool Hidden
        {
            get => _hidden;
            set
            {
                if (SetValueAndReturn(ref _hidden, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(HiddenTitleSuffix));
                    OnPropertyChanged(nameof(DescriptionResolved));
                }
            }
        }

        private string _apiName;
        public string ApiName { get => _apiName; set => SetValue(ref _apiName, value); }

        // Hidden achievement visibility settings
        private bool _showHiddenIcon;
        public bool ShowHiddenIcon
        {
            get => _showHiddenIcon;
            set
            {
                if (SetValueAndReturn(ref _showHiddenIcon, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(IsIconHidden));
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        private bool _showHiddenTitle;
        public bool ShowHiddenTitle
        {
            get => _showHiddenTitle;
            set
            {
                if (SetValueAndReturn(ref _showHiddenTitle, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(IsTitleHidden));
                    OnPropertyChanged(nameof(DisplayNameResolved));
                    OnPropertyChanged(nameof(HiddenTitleSuffix));
                }
            }
        }

        private bool _showHiddenDescription;
        public bool ShowHiddenDescription
        {
            get => _showHiddenDescription;
            set
            {
                if (SetValueAndReturn(ref _showHiddenDescription, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    OnPropertyChanged(nameof(DescriptionResolved));
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

        private bool _showLockedIcon;
        public bool ShowLockedIcon
        {
            get => _showLockedIcon;
            set
            {
                if (SetValueAndReturn(ref _showLockedIcon, value))
                {
                    OnPropertyChanged(nameof(CanReveal));
                    OnPropertyChanged(nameof(IsLockedIconHidden));
                    OnPropertyChanged(nameof(DisplayIcon));
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        private bool _showRarityGlow = true;
        public bool ShowRarityGlow
        {
            get => _showRarityGlow;
            set => SetValue(ref _showRarityGlow, value);
        }

        private bool _showRarityBar = true;
        public bool ShowRarityBar
        {
            get => _showRarityBar;
            set => SetValue(ref _showRarityBar, value);
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
                    OnPropertyChanged(nameof(IsHidden));
                    OnPropertyChanged(nameof(IsIconHidden));
                    OnPropertyChanged(nameof(IsLockedIconHidden));
                    OnPropertyChanged(nameof(IsTitleHidden));
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    // Only notify icon changes if icon hiding is enabled (hidden or locked)
                    if (!ShowHiddenIcon || !ShowLockedIcon)
                    {
                        OnPropertyChanged(nameof(DisplayIcon));
                        OnPropertyChanged(nameof(Icon));
                    }
                    // Only notify name changes if title hiding is enabled
                    if (!ShowHiddenTitle)
                    {
                        OnPropertyChanged(nameof(DisplayNameResolved));
                        OnPropertyChanged(nameof(HiddenTitleSuffix));
                    }
                    // Only notify description changes if description hiding is enabled
                    if (!ShowHiddenDescription)
                    {
                        OnPropertyChanged(nameof(DescriptionResolved));
                    }
                }
            }
        }

        private int? _progressNum;
        public int? ProgressNum
        {
            get => _progressNum;
            set
            {
                if (SetValueAndReturn(ref _progressNum, value))
                {
                    OnPropertyChanged(nameof(HasProgress));
                    OnPropertyChanged(nameof(ProgressText));
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
        }

        private int? _progressDenom;
        public int? ProgressDenom
        {
            get => _progressDenom;
            set
            {
                if (SetValueAndReturn(ref _progressDenom, value))
                {
                    OnPropertyChanged(nameof(HasProgress));
                    OnPropertyChanged(nameof(ProgressText));
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
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

        private string _gameIconPath;
        /// <summary>
        /// Path to the game's icon image.
        /// Used by the Game column in sidebar recent achievements.
        /// </summary>
        public string GameIconPath
        {
            get => _gameIconPath;
            set => SetValue(ref _gameIconPath, value);
        }

        private string _gameCoverPath;
        /// <summary>
        /// Path to the game's cover image.
        /// Used by the Game column in sidebar recent achievements when UseCoverImages is true.
        /// </summary>
        public string GameCoverPath
        {
            get => _gameCoverPath;
            set => SetValue(ref _gameCoverPath, value);
        }

        /// <summary>
        /// True if this achievement has PlayStation trophy type data.
        /// </summary>
        public bool HasTrophyType => !string.IsNullOrWhiteSpace(TrophyType);

        /// <summary>
        /// True if this achievement has progress data (both numerator and denominator are set).
        /// </summary>
        public bool HasProgress => ProgressNum.HasValue && ProgressDenom.HasValue && ProgressDenom.Value > 0;

        /// <summary>
        /// Text representation of progress as "ProgressNum / ProgressDenom".
        /// </summary>
        public string ProgressText => HasProgress ? $"{ProgressNum.Value} / {ProgressDenom.Value}" : string.Empty;

        /// <summary>
        /// Progress percentage (0-100) for progress bar binding.
        /// Returns 0 when no progress data exists.
        /// </summary>
        public double ProgressPercent => HasProgress ? (ProgressNum.Value * 100.0 / ProgressDenom.Value) : 0;

        /// <summary>
        /// True if the achievement can be revealed (is locked and at least one hiding setting is enabled).
        /// </summary>
        /// <summary>
        /// True if the achievement can be revealed (is locked and at least one hiding setting is enabled).
        /// Includes both hidden achievements and locked achievements when ShowLockedIcon is false.
        /// </summary>
        public bool CanReveal => !Unlocked && (!ShowLockedIcon || (Hidden && (!ShowHiddenIcon || !ShowHiddenTitle || !ShowHiddenDescription)));

        /// <summary>
        /// True if the achievement details are currently hidden (can reveal and not yet revealed).
        /// </summary>
        public bool IsHidden => CanReveal && !IsRevealed;

        /// <summary>
        /// True if the icon is currently being hidden due to hidden achievement settings (for XAML styling triggers).
        /// </summary>
        public bool IsIconHidden => IsHidden && Hidden && !ShowHiddenIcon;

        /// <summary>
        /// True if the icon is currently being hidden due to locked achievement settings (for XAML styling triggers).
        /// </summary>
        public bool IsLockedIconHidden => !Unlocked && !ShowLockedIcon && !IsRevealed;

        /// <summary>
        /// True if the title is currently being hidden (for XAML styling triggers).
        /// </summary>
        public bool IsTitleHidden => IsHidden && Hidden && !ShowHiddenTitle;

        /// <summary>
        /// True if the description is currently being hidden (for XAML styling triggers).
        /// </summary>
        public bool IsDescriptionHidden => IsHidden && !ShowHiddenDescription;

        public string DisplayNameResolved
        {
            get
            {
                if (IsHidden && Hidden && !ShowHiddenTitle) return ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle");
                return DisplayName;
            }
        }

        public string HiddenTitleSuffix
        {
            get
            {
                if (ShowHiddenSuffix && Hidden && !IsTitleHidden) return ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle_WithParens");
                return string.Empty;
            }
        }

        public string DescriptionResolved
        {
            get
            {
                if (IsHidden && Hidden && !ShowHiddenDescription) return ResourceProvider.GetString("LOCPlayAch_Achievements_ClickToReveal");
                return Description;
            }
        }

        /// <summary>
        /// Toggles the revealed state if the achievement can be revealed.
        /// </summary>
        public void ToggleReveal()
        {
            if (CanReveal)
            {
                IsRevealed = !IsRevealed;
            }
        }
        
        /// <summary>
        /// Updates this item's properties from a source Achievement object.
        /// This is used to synchronize data without recreating the entire object, preventing UI flicker.
        /// </summary>
        public void UpdateFrom(Models.Achievements.AchievementDetail source, string gameName, Guid? playniteGameId, bool hideIcon, bool hideTitle, bool hideDescription, bool hideLockedIcon, bool showRarityGlow, bool showRarityBar = true, string sortingName = null, string gameIconPath = null, string gameCoverPath = null)
        {
            ProviderKey = source.ProviderKey;
            GameName = gameName;
            SortingName = sortingName ?? gameName;
            PlayniteGameId = playniteGameId;
            DisplayName = source.DisplayName ?? source.ApiName ?? "Unknown Achievement";
            Description = source.Description ?? "No description";
            IconPath = source.UnlockedIconPath;
            UnlockTimeUtc = source.UnlockTimeUtc;
            GlobalPercentUnlocked = source.GlobalPercentUnlocked;
            Rarity = source.Rarity;
            Unlocked = source.Unlocked;
            Hidden = source.Hidden;
            ApiName = source.ApiName;
            ShowHiddenIcon = !hideIcon;
            ShowHiddenTitle = !hideTitle;
            ShowHiddenDescription = !hideDescription;
            ShowLockedIcon = !hideLockedIcon;
            ShowRarityGlow = showRarityGlow;
            ShowRarityBar = showRarityBar;
            ProgressNum = source.ProgressNum;
            ProgressDenom = source.ProgressDenom;
            PointsValue = source.Points;
            TrophyType = source.TrophyType;
            CategoryType = source.CategoryType;
            CategoryLabel = source.Category;
            GameIconPath = gameIconPath;
            GameCoverPath = gameCoverPath;
        }

        public string UnlockTimeText =>
            UnlockTimeUtc.HasValue ? $"{DateTimeUtilities.AsLocalFromUtc(UnlockTimeUtc.Value):g}" : string.Empty;

        /// <summary>
        /// True if this achievement has a real rarity percentage.
        /// </summary>
        public bool HasRarityPercent => GlobalPercentUnlocked.HasValue;

        public string GlobalPercentText => AchievementRarityResolver.GetDisplayText(GlobalPercentUnlocked, Rarity);

        public string RarityDetailText => AchievementRarityResolver.GetDetailText(GlobalPercentUnlocked, Rarity);

        public int Points => PointsValue ?? 0;

        public string PointsText => PointsValue.HasValue ? PointsValue.Value.ToString() : "-";

        private static string DefaultIcon => "pack://application:,,,/PlayniteAchievements;component/Resources/HiddenAchIcon.png";

        /// <summary>
        /// Returns the appropriate icon based on unlock state and hide settings.
        /// When hiding is enabled and achievement is locked and not revealed, shows the gray/hidden icon.
        /// Otherwise, uses IconPath with grayscale prefix when locked.
        /// </summary>
        public string DisplayIcon
        {
            get
            {
                // Hidden achievement icon hiding (takes precedence)
                if (IsHidden && Hidden && !ShowHiddenIcon)
                {
                    return DefaultIcon;
                }

                // Locked achievement icon hiding
                if (!Unlocked && !ShowLockedIcon && !IsRevealed)
                {
                    return DefaultIcon;
                }

                var candidate = IconPath;
                if (!Unlocked && !string.IsNullOrWhiteSpace(candidate))
                {
                    candidate = AchievementIconResolver.ApplyGrayPrefix(candidate);
                }

                return !string.IsNullOrWhiteSpace(candidate) ? candidate : DefaultIcon;
            }
        }

        private static bool IsHttpUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The unlock time for sorting purposes.
        /// </summary>
        public DateTime UnlockTime => UnlockTimeUtc ?? DateTime.MinValue;

        /// <summary>
        /// The global percent for sorting purposes.
        /// </summary>
        public double GlobalPercent => GlobalPercentUnlocked ?? 0;

        public double RarityPercentValue => GlobalPercentUnlocked ?? 0;

        public double RaritySortValue => AchievementRarityResolver.GetSortValue(GlobalPercentUnlocked, Rarity);

        // --- Theme integration compatibility (SuccessStory-style bindings) ---

        /// <summary>
        /// Alias for themes expecting a "Name" field (e.g. SuccessStory).
        /// </summary>
        public string Name => DisplayName;

        /// <summary>
        /// Alias for themes expecting an "Icon" field (e.g. SuccessStory).
        /// </summary>
        public string Icon => DisplayIcon;

        /// <summary>
        /// Alias for themes expecting a numeric "Percent" field (0-100).
        /// </summary>
        public double Percent => RarityPercentValue;

        /// <summary>
        /// Alias for themes expecting a "DateUnlocked" field (local time).
        /// </summary>
        public DateTime? DateUnlocked => UnlockTimeUtc.HasValue ? DateTimeUtilities.AsLocalFromUtc(UnlockTimeUtc.Value) : (DateTime?)null;

        /// <summary>
        /// Alias for themes expecting an "IsUnlock" boolean.
        /// </summary>
        public bool IsUnlock => Unlocked;

        /// <summary>
        /// Theme-facing score used by some themes to select trophy visuals.
        /// This intentionally maps rarity tiers to a small set of expected values.
        /// </summary>
        public int GamerScore
        {
            get
            {
                return Rarity switch
                {
                    RarityTier.UltraRare => 180,
                    RarityTier.Rare => 90,
                    RarityTier.Uncommon => 50,
                    _ => 25
                };
            }
        }

        /// <summary>
        /// Alias for themes expecting ImageUnlocked field (SuccessStory compatibility).
        /// Always returns IconPath (unlocked state uses the color icon).
        /// </summary>
        public string ImageUnlocked
        {
            get
            {
                var candidate = IconPath;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return DefaultIcon;
                }

                return candidate;
            }
        }

        /// <summary>
        /// Alias for themes expecting ImageLocked field (SuccessStory compatibility).
        /// Returns IconPath with grayscale prefix for locked state.
        /// </summary>
        public string ImageLocked
        {
            get
            {
                var candidate = IconPath;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return DefaultIcon;
                }

                return AchievementIconResolver.ApplyGrayPrefix(candidate);
            }
        }

        /// <summary>
        /// Creates a shallow copy of this display item with independent reveal state.
        /// Used when controls need their own item instances to avoid shared state.
        /// </summary>
        public AchievementDisplayItem Clone()
        {
            return new AchievementDisplayItem
            {
                ProviderKey = _providerKey,
                GameName = _gameName,
                SortingName = _sortingName,
                PlayniteGameId = _playniteGameId,
                DisplayName = _displayName,
                Description = _description,
                IconPath = _iconPath,
                UnlockTimeUtc = _unlockTimeUtc,
                GlobalPercentUnlocked = _globalPercentUnlocked,
                Rarity = _rarity,
                PointsValue = _pointsValue,
                Unlocked = _unlocked,
                Hidden = _hidden,
                ApiName = _apiName,
                ShowHiddenIcon = _showHiddenIcon,
                ShowHiddenTitle = _showHiddenTitle,
                ShowHiddenDescription = _showHiddenDescription,
                ShowHiddenSuffix = _showHiddenSuffix,
                ShowLockedIcon = _showLockedIcon,
                ShowRarityGlow = _showRarityGlow,
                ShowRarityBar = _showRarityBar,
                // IsRevealed intentionally not copied - each clone starts unrevealed
                ProgressNum = _progressNum,
                ProgressDenom = _progressDenom,
                TrophyType = _trophyType,
                CategoryType = _categoryType,
                CategoryLabel = _categoryLabel,
                GameIconPath = _gameIconPath,
                GameCoverPath = _gameCoverPath
            };
        }
    }
}
