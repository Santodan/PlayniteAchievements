using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Windows.Input;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using Playnite.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// Thin display wrapper around <see cref="AchievementDetail"/> for view-only state.
    /// </summary>
    public class AchievementDisplayItem : ObservableObject
    {
        public sealed class AppearanceSettingsSnapshot
        {
            public bool ShowHiddenIcon { get; set; }

            public bool ShowHiddenTitle { get; set; }

            public bool ShowHiddenDescription { get; set; }

            public bool ShowHiddenSuffix { get; set; }

            public bool ShowLockedIcon { get; set; }

            public bool UseSeparateLockedIconsWhenAvailable { get; set; }

            public bool ShowRarityBar { get; set; }

            public bool ShowFriendSpoilers { get; set; }
        }

        private AchievementDetail _source;
        private string _gameName;
        private string _sortingName;
        private Guid? _playniteGameId;
        private string _providerKey;
        private string _friendName;
        private string _friendExternalUserId;
        private string _friendAvatarPath;
        private bool _friendIsFavorite;
        private int? _pointsValue;
        private string _categoryType;
        private string _categoryLabel;
        private string _gameIconPath;
        private string _gameCoverPath;
        private int _categoryOrderIndex = int.MaxValue;
        private string _categoryArtPath;

        public AchievementDetail Source => _source;

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicAchievementsGameCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand FilterDynamicLibraryAchievementsByProviderCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand OpenViewAchievementsWindow { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand OpenManageAchievementsWindow { get; set; }

        public string GameName { get => _gameName; set => SetValue(ref _gameName, value); }

        public string SortingName { get => _sortingName; set => SetValue(ref _sortingName, value); }

        public Guid? PlayniteGameId { get => _playniteGameId; set => SetValue(ref _playniteGameId, value); }

        public string ProviderKey
        {
            get => _providerKey;
            set => SetValue(ref _providerKey, value);
        }

        public string FriendName
        {
            get => _friendName;
            set => SetValue(ref _friendName, value);
        }

        // Session-only: true when this achievement has any saved unlock captures on disk. Set by the
        // capture presence marker after the grid is built; gates the Captures column button.
        private bool _hasCaptures;
        [DontSerialize]
        [IgnoreDataMember]
        public bool HasCaptures { get => _hasCaptures; set => SetValue(ref _hasCaptures, value); }

        public string FriendExternalUserId
        {
            get => _friendExternalUserId;
            set => SetValue(ref _friendExternalUserId, value);
        }

        public string FriendAvatarPath
        {
            get => _friendAvatarPath;
            set => SetValue(ref _friendAvatarPath, value);
        }

        // Projected from the friend's persisted favorite flag; drives the favorite star in the
        // friend achievements grid's Friend column. Always false for self-achievement rows.
        public bool FriendIsFavorite
        {
            get => _friendIsFavorite;
            set => SetValue(ref _friendIsFavorite, value);
        }

        // --- Friend comparison (session-only; set by the hosting view model when a
        //     compare friend is selected, cleared when the selection changes) ---

        private bool _hasComparison;
        private string _comparisonFriendName;
        private string _comparisonFriendAvatarPath;
        private string _comparisonOwnerName;
        private string _comparisonOwnerAvatarPath;
        private DateTime? _comparisonUnlockTimeUtc;
        private bool _comparisonUnlocked;

        [DontSerialize]
        [IgnoreDataMember]
        public bool HasComparison
        {
            get => _hasComparison;
            private set => SetValue(ref _hasComparison, value);
        }

        [DontSerialize]
        [IgnoreDataMember]
        public string ComparisonFriendName
        {
            get => _comparisonFriendName;
            private set
            {
                if (SetValueAndReturn(ref _comparisonFriendName, value))
                {
                    OnPropertyChanged(nameof(ComparisonToolTip));
                }
            }
        }

        [DontSerialize]
        [IgnoreDataMember]
        public string ComparisonFriendAvatarPath
        {
            get => _comparisonFriendAvatarPath;
            private set => SetValue(ref _comparisonFriendAvatarPath, value);
        }

        // The identity of the row's own side of the comparison: the signed-in user on the self
        // achievement grids, and the selected friend in the friends-overview pair view. Null when
        // the row's provider has no cached avatar, which renders as an empty circle so both sides
        // stay aligned.
        [DontSerialize]
        [IgnoreDataMember]
        public string ComparisonOwnerName
        {
            get => _comparisonOwnerName;
            private set
            {
                if (SetValueAndReturn(ref _comparisonOwnerName, value))
                {
                    OnPropertyChanged(nameof(ComparisonToolTip));
                }
            }
        }

        [DontSerialize]
        [IgnoreDataMember]
        public string ComparisonOwnerAvatarPath
        {
            get => _comparisonOwnerAvatarPath;
            private set => SetValue(ref _comparisonOwnerAvatarPath, value);
        }

        [DontSerialize]
        [IgnoreDataMember]
        public DateTime? ComparisonUnlockTimeUtc
        {
            get => _comparisonUnlockTimeUtc;
            private set
            {
                if (SetValueAndReturn(ref _comparisonUnlockTimeUtc, value))
                {
                    OnPropertyChanged(nameof(ComparisonUnlockTimeLocal));
                    OnPropertyChanged(nameof(ComparisonToolTip));
                }
            }
        }

        [DontSerialize]
        [IgnoreDataMember]
        public bool ComparisonUnlocked
        {
            get => _comparisonUnlocked;
            private set
            {
                if (SetValueAndReturn(ref _comparisonUnlocked, value))
                {
                    OnPropertyChanged(nameof(ComparisonToolTip));
                }
            }
        }

        [DontSerialize]
        [IgnoreDataMember]
        public DateTime? ComparisonUnlockTimeLocal =>
            _comparisonUnlockTimeUtc.HasValue
                ? DateTimeUtilities.AsLocalFromUtc(_comparisonUnlockTimeUtc.Value)
                : (DateTime?)null;

        [DontSerialize]
        [IgnoreDataMember]
        public string ComparisonToolTip
        {
            get
            {
                if (!_hasComparison || string.IsNullOrWhiteSpace(_comparisonFriendName))
                {
                    return null;
                }

                var friendLine = FormatComparisonLine(
                    _comparisonFriendName,
                    _comparisonUnlocked,
                    ComparisonUnlockTimeLocal);
                if (string.IsNullOrWhiteSpace(_comparisonOwnerName))
                {
                    return friendLine;
                }

                // Both sides are named so the two avatars in the cell are identifiable.
                var ownerLine = FormatComparisonLine(_comparisonOwnerName, Unlocked, UnlockTimeLocal);
                return ownerLine + Environment.NewLine + friendLine;
            }
        }

        private static string FormatComparisonLine(string name, bool unlocked, DateTime? unlockTimeLocal)
        {
            if (!unlocked)
            {
                return $"{name} · {ResourceProvider.GetString("LOCPlayAch_Common_Locked")}";
            }

            return unlockTimeLocal.HasValue
                ? $"{name} · {unlockTimeLocal.Value.ToString("G", FormattingCulture.Current)}"
                : name;
        }

        public void ApplyComparison(
            string friendName,
            string friendAvatarPath,
            DateTime? unlockTimeUtc,
            bool unlocked,
            string ownerName = null,
            string ownerAvatarPath = null)
        {
            ComparisonFriendName = friendName;
            ComparisonFriendAvatarPath = friendAvatarPath;
            ComparisonUnlockTimeUtc = unlockTimeUtc;
            ComparisonUnlocked = unlocked;
            ComparisonOwnerName = ownerName;
            ComparisonOwnerAvatarPath = ownerAvatarPath;
            HasComparison = true;
        }

        public void ClearComparison()
        {
            HasComparison = false;
            ComparisonFriendName = null;
            ComparisonFriendAvatarPath = null;
            ComparisonUnlockTimeUtc = null;
            ComparisonUnlocked = false;
            ComparisonOwnerName = null;
            ComparisonOwnerAvatarPath = null;
        }

        public string DisplayName
        {
            get => _source?.DisplayName;
            set
            {
                if (SetSourceValue(
                    source => source.DisplayName,
                    (source, next) => source.DisplayName = next,
                    value,
                    nameof(DisplayName),
                    nameof(Name)))
                {
                    NotifyTitleDisplayChanged();
                }
            }
        }

        public string Description
        {
            get => _source?.Description;
            set
            {
                if (SetSourceValue(
                    source => source.Description,
                    (source, next) => source.Description = next,
                    value,
                    nameof(Description)))
                {
                    NotifyDescriptionDisplayChanged();
                }
            }
        }

        public string UnlockedIconPath
        {
            get => _source?.UnlockedIconPath;
            set
            {
                if (SetSourceValue(
                    source => source.UnlockedIconPath,
                    (source, next) => source.UnlockedIconPath = next,
                    value,
                    nameof(UnlockedIconPath)))
                {
                    NotifyIconPathChanged();
                }
            }
        }

        public string LockedIconPath
        {
            get => _source?.LockedIconPath;
            set
            {
                if (SetSourceValue(
                    source => source.LockedIconPath,
                    (source, next) => source.LockedIconPath = next,
                    value,
                    nameof(LockedIconPath)))
                {
                    NotifyIconPathChanged();
                }
            }
        }

        public string IconPath
        {
            get => UnlockedIconPath;
            set => UnlockedIconPath = value;
        }

        public DateTime? UnlockTimeUtc
        {
            get => _source?.UnlockTimeUtc;
            set
            {
                if (SetSourceValue(
                    source => source.UnlockTimeUtc,
                    (source, next) => source.UnlockTimeUtc = next,
                    value,
                    nameof(UnlockTimeUtc)))
                {
                    OnPropertyChanged(nameof(UnlockTimeText));
                    OnPropertyChanged(nameof(UnlockTimeLocal));
                    OnPropertyChanged(nameof(DateUnlocked));
                    OnPropertyChanged(nameof(UnlockTime));
                    OnPropertyChanged(nameof(ShowUnlockDate));
                    OnPropertyChanged(nameof(ShowLockedProgress));
                }
            }
        }

        public double? GlobalPercentUnlocked
        {
            get => _source?.GlobalPercentUnlocked;
            set
            {
                if (SetSourceValue(
                    source => source.GlobalPercentUnlocked,
                    (source, next) => source.GlobalPercentUnlocked = next,
                    value,
                    nameof(GlobalPercentUnlocked)))
                {
                    OnPropertyChanged(nameof(HasRarityPercent));
                    OnPropertyChanged(nameof(GlobalPercentText));
                    OnPropertyChanged(nameof(RarityDetailText));
                    OnPropertyChanged(nameof(GlobalPercent));
                    OnPropertyChanged(nameof(RarityPercentValue));
                    OnPropertyChanged(nameof(Percent));
                    OnPropertyChanged(nameof(RaritySortValue));
                    OnPropertyChanged(nameof(PrestigeScore));
                }
            }
        }

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

        public RarityTier Rarity
        {
            get => _source?.Rarity ?? RarityTier.Common;
            set
            {
                if (SetSourceValue(
                    source => source.Rarity,
                    (source, next) => source.Rarity = next,
                    value,
                    nameof(Rarity)))
                {
                    OnPropertyChanged(nameof(GlobalPercentText));
                    OnPropertyChanged(nameof(RarityDetailText));
                    OnPropertyChanged(nameof(GamerScore));
                    OnPropertyChanged(nameof(RaritySortValue));
                    OnPropertyChanged(nameof(CollectionScore));
                    OnPropertyChanged(nameof(PrestigeScore));
                    OnPropertyChanged(nameof(RarityBrush));
                    OnPropertyChanged(nameof(RarityNameBrush));
                }
            }
        }

        public bool Unlocked
        {
            get => _source?.Unlocked == true;
            set
            {
                if (SetSourceValue(
                    source => source.Unlocked,
                    (source, next) => source.Unlocked = next,
                    value,
                    nameof(Unlocked)))
                {
                    NotifyRevealStateChanged();
                    NotifyIconDisplayChanged();
                    NotifyTitleDisplayChanged();
                    NotifyDescriptionDisplayChanged();
                    OnPropertyChanged(nameof(ShowUnlockDate));
                    OnPropertyChanged(nameof(ShowLockedProgress));
                    OnPropertyChanged(nameof(IsUnlock));
                    OnPropertyChanged(nameof(UnlockedForVisibility));
                }
            }
        }

        public virtual bool ShowUnlockDate => Unlocked;

        public virtual bool ShowLockedProgress => !ShowUnlockDate;

        public bool IsCapstone
        {
            get => _source?.IsCapstone == true;
            set
            {
                if (SetSourceValue(
                    source => source.IsCapstone,
                    (source, next) => source.IsCapstone = next,
                    value,
                    nameof(IsCapstone)))
                {
                    OnPropertyChanged(nameof(RarityNameBrush));
                }
            }
        }

        public bool IsGoal
        {
            get => _source?.IsGoal == true;
            set
            {
                SetSourceValue(
                    source => source.IsGoal,
                    (source, next) => source.IsGoal = next,
                    value,
                    nameof(IsGoal));
            }
        }

        public int GoalOrderIndex
        {
            get => _source?.GoalOrderIndex ?? int.MaxValue;
            set
            {
                SetSourceValue(
                    source => source.GoalOrderIndex,
                    (source, next) => source.GoalOrderIndex = next,
                    value,
                    nameof(GoalOrderIndex));
            }
        }

        public bool Hidden
        {
            get => _source?.Hidden == true;
            set
            {
                if (SetSourceValue(
                    source => source.Hidden,
                    (source, next) => source.Hidden = next,
                    value,
                    nameof(Hidden)))
                {
                    NotifyRevealStateChanged();
                    NotifyIconDisplayChanged();
                    NotifyTitleDisplayChanged();
                    NotifyDescriptionDisplayChanged();
                }
            }
        }

        public string ApiName
        {
            get => _source?.ApiName;
            set
            {
                if (SetSourceValue(
                    source => source.ApiName,
                    (source, next) => source.ApiName = next,
                    value,
                    nameof(ApiName)))
                {
                    OnPropertyChanged(nameof(ApiNameResolved));
                }
            }
        }

        public string AchievementNote
        {
            get => _source?.AchievementNote;
            set
            {
                SetSourceValue(
                    source => source.AchievementNote,
                    (source, next) => source.AchievementNote = next,
                    AchievementNoteHelper.NormalizeNote(value),
                    nameof(AchievementNote),
                    nameof(HasAchievementNote),
                    nameof(AchievementNoteInlineMarker));
            }
        }

        public bool HasAchievementNote => !string.IsNullOrWhiteSpace(AchievementNote);

        public string AchievementNoteInlineMarker => HasAchievementNote ? " \uEFB6" : string.Empty;

        // Hidden achievement visibility settings
        private bool _showHiddenIcon;
        public bool ShowHiddenIcon
        {
            get => _showHiddenIcon;
            set
            {
                if (SetValueAndReturn(ref _showHiddenIcon, value))
                {
                    NotifyRevealStateChanged();
                    OnPropertyChanged(nameof(IsIconHidden));
                    NotifyIconDisplayChanged();
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
                    NotifyRevealStateChanged();
                    OnPropertyChanged(nameof(IsTitleHidden));
                    NotifyTitleDisplayChanged();
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
                    NotifyRevealStateChanged();
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    NotifyDescriptionDisplayChanged();
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
                    NotifyTitleDisplayChanged();
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
                    NotifyRevealStateChanged();
                    OnPropertyChanged(nameof(IsLockedIconHidden));
                    NotifyIconDisplayChanged();
                }
            }
        }

        private bool _showFriendSpoilers;
        public bool ShowFriendSpoilers
        {
            get => _showFriendSpoilers;
            set
            {
                if (SetValueAndReturn(ref _showFriendSpoilers, value))
                {
                    NotifyRevealStateChanged();
                    OnPropertyChanged(nameof(IsIconHidden));
                    OnPropertyChanged(nameof(IsLockedIconHidden));
                    OnPropertyChanged(nameof(IsTitleHidden));
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    NotifyIconDisplayChanged();
                    NotifyTitleDisplayChanged();
                    NotifyDescriptionDisplayChanged();
                }
            }
        }

        private bool _useSeparateLockedIconsWhenAvailable;
        public bool UseSeparateLockedIconsWhenAvailable
        {
            get => _useSeparateLockedIconsWhenAvailable;
            set
            {
                if (SetValueAndReturn(ref _useSeparateLockedIconsWhenAvailable, value))
                {
                    OnPropertyChanged(nameof(UsesExplicitLockedIcon));
                    NotifyIconDisplayChanged();
                    OnPropertyChanged(nameof(ImageLocked));
                }
            }
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
                    NotifyRevealStateChanged();
                    OnPropertyChanged(nameof(IsIconHidden));
                    OnPropertyChanged(nameof(IsLockedIconHidden));
                    OnPropertyChanged(nameof(IsTitleHidden));
                    OnPropertyChanged(nameof(IsDescriptionHidden));
                    if (!ShowHiddenIcon || !ShowLockedIcon)
                    {
                        NotifyIconDisplayChanged();
                    }
                    if (!ShowHiddenTitle)
                    {
                        NotifyTitleDisplayChanged();
                    }
                    if (!ShowHiddenDescription)
                    {
                        NotifyDescriptionDisplayChanged();
                    }
                }
            }
        }

        public int? ProgressNum
        {
            get => _source?.ProgressNum;
            set
            {
                if (SetSourceValue(
                    source => source.ProgressNum,
                    (source, next) => source.ProgressNum = next,
                    value,
                    nameof(ProgressNum)))
                {
                    OnPropertyChanged(nameof(HasProgress));
                    OnPropertyChanged(nameof(ProgressText));
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
        }

        public int? ProgressDenom
        {
            get => _source?.ProgressDenom;
            set
            {
                if (SetSourceValue(
                    source => source.ProgressDenom,
                    (source, next) => source.ProgressDenom = next,
                    value,
                    nameof(ProgressDenom)))
                {
                    OnPropertyChanged(nameof(HasProgress));
                    OnPropertyChanged(nameof(ProgressText));
                    OnPropertyChanged(nameof(ProgressPercent));
                }
            }
        }

        /// <summary>
        /// Trophy type for PlayStation games: "bronze", "silver", "gold", "platinum".
        /// Null for non-PlayStation achievements.
        /// </summary>
        public string TrophyType
        {
            get => _source?.TrophyType;
            set
            {
                if (SetSourceValue(
                    source => source.TrophyType,
                    (source, next) => source.TrophyType = next,
                    value,
                    nameof(TrophyType)))
                {
                    OnPropertyChanged(nameof(HasTrophyType));
                }
            }
        }

        public string CategoryType
        {
            get => _categoryType;
            set
            {
                if (SetValueAndReturn(ref _categoryType, value))
                {
                    OnPropertyChanged(nameof(CategoryTypeDisplay));
                    OnPropertyChanged(nameof(IsHardcore));
                }
            }
        }

        public string CategoryTypeDisplay => AchievementCategoryTypeHelper.ToDisplayText(CategoryType);

        /// <summary>
        /// True when the achievement is classified as Hardcore (RetroAchievements unlock mode).
        /// Drives the solid rarity border in place of the soft glow. Robust to category types
        /// combined with other values.
        /// </summary>
        public bool IsHardcore =>
            AchievementCategoryTypeHelper.ParseValues(CategoryType)
                .Contains(AchievementCategoryTypeHelper.HardcoreCategoryType);

        public string CategoryLabel
        {
            get => _categoryLabel;
            set
            {
                if (SetValueAndReturn(ref _categoryLabel, value))
                {
                    OnPropertyChanged(nameof(CategoryLabelDisplay));
                }
            }
        }

        public string CategoryLabelDisplay => AchievementCategoryTypeHelper.ToCategoryLabelCellText(CategoryLabel);

        /// <summary>
        /// Path to the game's icon image.
        /// Used by the Game column in overview recent achievements.
        /// </summary>
        public string GameIconPath
        {
            get => _gameIconPath;
            set
            {
                if (SetValueAndReturn(ref _gameIconPath, value))
                {
                    OnPropertyChanged(nameof(CategoryIconDisplayPath));
                }
            }
        }

        /// <summary>
        /// Path to the game's cover image.
        /// Used by the Game column in overview recent achievements when UseCoverImages is true.
        /// </summary>
        public string GameCoverPath
        {
            get => _gameCoverPath;
            set
            {
                if (SetValueAndReturn(ref _gameCoverPath, value))
                {
                    OnPropertyChanged(nameof(CategoryCoverDisplayPath));
                }
            }
        }

        public int CategoryOrderIndex
        {
            get => _categoryOrderIndex;
            set => SetValue(ref _categoryOrderIndex, value);
        }

        /// <summary>
        /// Category art path: custom override when set, otherwise the provider default.
        /// Null when neither exists; the display-path properties below choose the game
        /// icon/cover fallback per the grid's UseCoverImages setting.
        /// </summary>
        public string CategoryArtPath
        {
            get => _categoryArtPath;
            set
            {
                if (SetValueAndReturn(ref _categoryArtPath, value))
                {
                    OnPropertyChanged(nameof(CategoryIconDisplayPath));
                    OnPropertyChanged(nameof(CategoryCoverDisplayPath));
                }
            }
        }

        /// <summary>
        /// Category column binding target when the grid shows icons: art, else the game icon.
        /// </summary>
        public string CategoryIconDisplayPath => CategoryArtPath ?? GameIconPath;

        /// <summary>
        /// Category column binding target when the grid shows covers: art, else the game cover.
        /// </summary>
        public string CategoryCoverDisplayPath => CategoryArtPath ?? GameCoverPath;

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
        public string ProgressText => HasProgress
            ? $"{ProgressNum.Value.ToString("N0", FormattingCulture.Current)}/{ProgressDenom.Value.ToString("N0", FormattingCulture.Current)}"
            : string.Empty;

        /// <summary>
        /// Progress percentage (0-100) for progress bar binding.
        /// Returns 0 when no progress data exists.
        /// </summary>
        public double ProgressPercent => HasProgress ? (ProgressNum.Value * 100.0 / ProgressDenom.Value) : 0;

        /// <summary>
        /// Unlock state used for visibility/obscuring decisions.
        /// Subclasses may substitute a different unlock source (e.g. the current user's
        /// unlock state on friend items when spoiler protection is enabled).
        /// </summary>
        public virtual bool UnlockedForVisibility => Unlocked;

        /// <summary>
        /// True if the achievement can be revealed (is locked and at least one hiding setting is enabled).
        /// Includes both hidden achievements and locked achievements when ShowLockedIcon is false.
        /// </summary>
        public bool CanReveal => !UnlockedForVisibility && (!ShowLockedIcon || (Hidden && (!ShowHiddenIcon || !ShowHiddenTitle || !ShowHiddenDescription)));

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
        public bool IsLockedIconHidden => !UnlockedForVisibility && !ShowLockedIcon && !IsRevealed;

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

        /// <summary>
        /// Rarity-based foreground brush for the achievement name: the completed color for capstone
        /// achievements (which takes precedence), otherwise the rarity tier brush. Computed unconditionally;
        /// grids decide whether to use it via their per-grid ColorNamesByRarity toggle.
        /// </summary>
        public System.Windows.Media.Brush RarityNameBrush
        {
            get
            {
                var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                return IsCapstone
                    ? RarityAppearanceHelper.GetCompletedBrush(persisted)
                    : RarityAppearanceHelper.GetBrush(Rarity, persisted);
            }
        }

        public System.Windows.Media.Brush RarityBrush
        {
            get
            {
                var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                return RarityAppearanceHelper.GetBrush(Rarity, persisted);
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

        public string ApiNameResolved
        {
            get
            {
                if (IsHidden && Hidden && !ShowHiddenDescription) return string.Empty;
                return ApiName;
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
        public void UpdateFrom(
            Models.Achievements.AchievementDetail source,
            string gameName,
            Guid? playniteGameId,
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon,
            bool useSeparateLockedIconsWhenAvailable,
            bool showRarityBar = true,
            string sortingName = null,
            string gameIconPath = null,
            string gameCoverPath = null,
            int categoryOrderIndex = int.MaxValue,
            string categoryArtPath = null)
        {
            SetSource(source, notifyChanges: true);
            ProviderKey = source?.ProviderKey;
            GameName = gameName;
            SortingName = sortingName ?? gameName;
            PlayniteGameId = playniteGameId;
            ApplyAppearanceSettings(
                showHiddenIcon,
                showHiddenTitle,
                showHiddenDescription,
                showHiddenSuffix,
                showLockedIcon,
                useSeparateLockedIconsWhenAvailable,
                showRarityBar);
            PointsValue = source?.Points;
            CategoryType = source?.CategoryType;
            CategoryLabel = source?.Category;
            var resolvedGameIconPath = gameIconPath ?? ResolveGameAssetPath(source?.Game?.Icon);
            var resolvedGameCoverPath = gameCoverPath ?? ResolveGameAssetPath(source?.Game?.CoverImage);
            GameIconPath = resolvedGameIconPath;
            GameCoverPath = resolvedGameCoverPath;
            CategoryOrderIndex = categoryOrderIndex;
            // Deliberately no game-asset fallback here: null means "no category art" and the
            // display-path properties select the game icon/cover per the grid setting.
            CategoryArtPath = categoryArtPath ?? source?.CategoryArtPath;
        }

        /// <summary>
        /// Updates this item from another display item while keeping the current object instance.
        /// </summary>
        public void UpdateFrom(AchievementDisplayItem sourceItem)
        {
            if (sourceItem == null)
            {
                return;
            }

            UpdateFrom(
                sourceItem.Source,
                sourceItem.GameName,
                sourceItem.PlayniteGameId,
                sourceItem.ShowHiddenIcon,
                sourceItem.ShowHiddenTitle,
                sourceItem.ShowHiddenDescription,
                sourceItem.ShowHiddenSuffix,
                sourceItem.ShowLockedIcon,
                sourceItem.UseSeparateLockedIconsWhenAvailable,
                sourceItem.ShowRarityBar,
                sourceItem.SortingName,
                sourceItem.GameIconPath,
                sourceItem.GameCoverPath,
                sourceItem.CategoryOrderIndex,
                sourceItem.CategoryArtPath);
            ProviderKey = sourceItem.ProviderKey;
            PointsValue = sourceItem.PointsValue;
            CategoryType = sourceItem.CategoryType;
            CategoryLabel = sourceItem.CategoryLabel;
            IsRevealed = sourceItem.IsRevealed;
            ShowFriendSpoilers = sourceItem.ShowFriendSpoilers;
        }

        public void ApplyAppearanceSettings(
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon,
            bool useSeparateLockedIconsWhenAvailable,
            bool showRarityBar = true)
        {
            ShowHiddenIcon = showHiddenIcon;
            ShowHiddenTitle = showHiddenTitle;
            ShowHiddenDescription = showHiddenDescription;
            ShowHiddenSuffix = showHiddenSuffix;
            ShowLockedIcon = showLockedIcon;
            UseSeparateLockedIconsWhenAvailable = useSeparateLockedIconsWhenAvailable;
            ShowRarityBar = showRarityBar;
            OnPropertyChanged(nameof(RarityBrush));
            OnPropertyChanged(nameof(RarityNameBrush));
        }

        public void ApplyAppearanceSettings(PlayniteAchievementsSettings settings, Guid? playniteGameId = null)
        {
            ApplyAppearanceSettings(CreateAppearanceSettingsSnapshot(
                settings,
                playniteGameId,
                null));
        }

        public void ApplyAppearanceSettings(AppearanceSettingsSnapshot snapshot)
        {
            var resolved = snapshot ?? new AppearanceSettingsSnapshot();
            ApplyAppearanceSettings(
                resolved.ShowHiddenIcon,
                resolved.ShowHiddenTitle,
                resolved.ShowHiddenDescription,
                resolved.ShowHiddenSuffix,
                resolved.ShowLockedIcon,
                resolved.UseSeparateLockedIconsWhenAvailable,
                resolved.ShowRarityBar);
            ShowFriendSpoilers = resolved.ShowFriendSpoilers;
        }

        public static AppearanceSettingsSnapshot CreateAppearanceSettingsSnapshot(
            PlayniteAchievementsSettings settings,
            Guid? playniteGameId,
            bool? resolvedUseSeparateLockedIcons)
        {
            var persisted = settings?.Persisted;
            var resolvedGameId = playniteGameId;
            return new AppearanceSettingsSnapshot
            {
                ShowHiddenIcon = persisted?.ShowHiddenIcon ?? false,
                ShowHiddenTitle = persisted?.ShowHiddenTitle ?? false,
                ShowHiddenDescription = persisted?.ShowHiddenDescription ?? false,
                ShowHiddenSuffix = persisted?.ShowHiddenSuffix ?? true,
                ShowLockedIcon = persisted?.ShowLockedIcon ?? true,
                UseSeparateLockedIconsWhenAvailable = resolvedUseSeparateLockedIcons ??
                    GameCustomDataLookup.ShouldUseSeparateLockedIcons(resolvedGameId, persisted),
                ShowRarityBar = persisted?.ShowCompactListRarityBar ?? true,
                ShowFriendSpoilers = persisted?.ShowFriendSpoilers ?? false
            };
        }

        public string UnlockTimeText =>
            UnlockTimeUtc.HasValue ? $"{DateTimeUtilities.AsLocalFromUtc(UnlockTimeUtc.Value):g}" : string.Empty;

        // Local-time projection for grid display; formatting is applied by DateDisplayModeConverter.
        public DateTime? UnlockTimeLocal =>
            UnlockTimeUtc.HasValue ? DateTimeUtilities.AsLocalFromUtc(UnlockTimeUtc.Value) : (DateTime?)null;

        /// <summary>
        /// True if this achievement has a real rarity percentage.
        /// </summary>
        public bool HasRarityPercent => GlobalPercentUnlocked.HasValue;

        public string GlobalPercentText => AchievementRarityResolver.GetDisplayText(GlobalPercentUnlocked, Rarity);

        public string RarityDetailText => AchievementRarityResolver.GetDetailText(GlobalPercentUnlocked, Rarity);

        public int Points => PointsValue ?? 0;

        public string PointsText => PointsValue.GetValueOrDefault() != 0
            ? PointsValue.Value.ToString("N0", FormattingCulture.Current)
            : string.Empty;

        public int CollectionScore => _source?.CollectionScore ?? 0;

        public int PrestigeScore => _source?.PrestigeScore ?? 0;

        private static string DefaultIcon => AchievementIconResolver.GetDefaultIcon();

        /// <summary>
        /// Returns the appropriate icon based on unlock state and hide settings.
        /// When hiding is enabled and achievement is locked and not revealed, shows the placeholder icon.
        /// Otherwise, uses a real locked icon when available and enabled, or falls back to the grayscale unlocked icon.
        /// </summary>
        public string DisplayIcon
        {
            get
            {
                if (ShouldShowPlaceholderIcon())
                {
                    return DefaultIcon;
                }

                return Unlocked
                    ? AchievementIconResolver.GetUnlockedDisplayIcon(UnlockedIconPath)
                    : GetLockedDisplayIcon();
            }
        }

        public bool UsesExplicitLockedIcon =>
            AchievementIconResolver.HasExplicitLockedIcon(LockedIconPath, UnlockedIconPath);

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
        public string Icon => AchievementIconResolver.GetLegacyCompatibleIcon(UnlockedIconPath);

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
        /// Returns the unlocked display icon or the default placeholder.
        /// </summary>
        public string ImageUnlocked
        {
            get => AchievementIconResolver.GetUnlockedDisplayIcon(UnlockedIconPath);
        }

        /// <summary>
        /// Alias for themes expecting ImageLocked field (SuccessStory compatibility).
        /// Returns a real locked icon when enabled and available, otherwise a grayscale unlocked fallback.
        /// </summary>
        public string ImageLocked => GetLockedDisplayIcon();

        /// <summary>
        /// Creates a shallow copy of this display item with independent reveal state.
        /// Used when controls need their own item instances to avoid shared state.
        /// </summary>
        public AchievementDisplayItem Clone()
        {
            var clone = new AchievementDisplayItem();
            clone.SetSource(_source, notifyChanges: false);
            clone.ProviderKey = _providerKey;
            clone.FriendName = _friendName;
            clone.FriendExternalUserId = _friendExternalUserId;
            clone.FriendAvatarPath = _friendAvatarPath;
            clone.FriendIsFavorite = _friendIsFavorite;
            clone.GameName = _gameName;
            clone.SortingName = _sortingName;
            clone.PlayniteGameId = _playniteGameId;
            clone.PointsValue = _pointsValue;
            clone.ShowHiddenIcon = _showHiddenIcon;
            clone.ShowHiddenTitle = _showHiddenTitle;
            clone.ShowHiddenDescription = _showHiddenDescription;
            clone.ShowHiddenSuffix = _showHiddenSuffix;
            clone.ShowLockedIcon = _showLockedIcon;
            clone.UseSeparateLockedIconsWhenAvailable = _useSeparateLockedIconsWhenAvailable;
            clone.ShowRarityBar = _showRarityBar;
            clone.ShowFriendSpoilers = _showFriendSpoilers;
            clone.CategoryType = _categoryType;
            clone.CategoryLabel = _categoryLabel;
            clone.GameIconPath = _gameIconPath;
            clone.GameCoverPath = _gameCoverPath;
            clone.CategoryOrderIndex = _categoryOrderIndex;
            clone.CategoryArtPath = _categoryArtPath;
            clone.SetDynamicAchievementsGameCommand = SetDynamicAchievementsGameCommand;
            clone.FilterDynamicLibraryAchievementsByProviderCommand = FilterDynamicLibraryAchievementsByProviderCommand;
            clone.OpenViewAchievementsWindow = OpenViewAchievementsWindow;
            clone.OpenManageAchievementsWindow = OpenManageAchievementsWindow;
            return clone;
        }

        /// <summary>
        /// Per-rebuild-pass memo for category presentation (order index and resolved art path).
        /// Rows in one pass share a single game's category order, image overrides, and default
        /// art directory, so the resolution repeats per distinct category; without a memo every
        /// row pays override-path resolution and disk probing again. Scope one memo to a single
        /// pass over a single game's achievements and discard it afterwards.
        /// </summary>
        public sealed class CategoryPresentationMemo
        {
            internal sealed class Entry
            {
                public int OrderIndex { get; set; }
                public string ArtPath { get; set; }
            }

            private readonly Dictionary<string, Entry> _entries =
                new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            internal bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry);

            internal void Set(string key, Entry entry) => _entries[key] = entry;
        }

        public static AchievementDisplayItem Create(
            GameAchievementData gameData,
            AchievementDetail achievement,
            PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys = null,
            Guid? playniteGameIdOverride = null,
            AppearanceSettingsSnapshot appearanceSettings = null,
            CategoryPresentationMemo categoryMemo = null)
        {
            if (achievement == null)
            {
                return null;
            }

            var gameId = playniteGameIdOverride ?? gameData?.PlayniteGameId;
            var item = CreateBaseItem(gameData, achievement, gameId, ResolvePoints(achievement, gameData), categoryMemo);
            var resolvedAppearanceSettings = appearanceSettings ?? CreateAppearanceSettingsSnapshot(
                settings,
                gameId,
                gameData?.UseSeparateLockedIconsWhenAvailable);
            item.IsRevealed = ShouldRestoreRevealedState(gameData, achievement, settings, revealedKeys, gameId);
            item.ApplyAppearanceSettings(resolvedAppearanceSettings);
            return item;
        }

        public static AchievementDisplayItem CreateRecent(
            GameAchievementData gameData,
            AchievementDetail achievement,
            PlayniteAchievementsSettings settings,
            string gameIconPath,
            string gameCoverPath,
            AppearanceSettingsSnapshot appearanceSettings = null)
        {
            if (achievement == null || !achievement.Unlocked || !achievement.UnlockTimeUtc.HasValue)
            {
                return null;
            }

            var item = CreateBaseItem(
                gameData,
                achievement,
                gameData?.PlayniteGameId,
                ResolvePoints(achievement, gameData));
            item.GameIconPath = gameIconPath;
            item.GameCoverPath = gameCoverPath;
            ApplyCategoryPresentation(
                item,
                gameData,
                item.CategoryLabel,
                achievement.ProviderCategory,
                gameData?.PlayniteGameId);
            var resolvedAppearanceSettings = appearanceSettings ?? CreateAppearanceSettingsSnapshot(
                settings,
                gameData?.PlayniteGameId,
                gameData?.UseSeparateLockedIconsWhenAvailable);
            item.ApplyAppearanceSettings(resolvedAppearanceSettings);
            return item;
        }

        public static bool IsAppearanceSettingPropertyName(string propertyName)
        {
            switch (NormalizePersistedPropertyName(propertyName))
            {
                case nameof(PersistedSettings.ShowHiddenIcon):
                case nameof(PersistedSettings.ShowHiddenTitle):
                case nameof(PersistedSettings.ShowHiddenDescription):
                case nameof(PersistedSettings.ShowHiddenSuffix):
                case nameof(PersistedSettings.ShowLockedIcon):
                case nameof(PersistedSettings.UseSeparateLockedIconsWhenAvailable):
                case nameof(PersistedSettings.ShowFriendSpoilers):
                case nameof(PersistedSettings.SeparateLockedIconEnabledGameIds):
                case nameof(PersistedSettings.UseUniformRarityBadges):
                case nameof(PersistedSettings.RarityColors):
                    return true;
                default:
                    return false;
            }
        }

        public static string MakeRevealKey(Guid? playniteGameId, string apiName, string gameName)
        {
            var gamePart = playniteGameId?.ToString() ?? (gameName ?? string.Empty);
            return $"{gamePart}\u001f{apiName ?? string.Empty}";
        }

        public static void AccumulateRarity(AchievementDetail achievement, ref int common, ref int uncommon, ref int rare, ref int ultraRare)
        {
            if (achievement == null)
            {
                return;
            }

            switch (achievement.Rarity)
            {
                case RarityTier.UltraRare:
                    ultraRare++;
                    break;
                case RarityTier.Rare:
                    rare++;
                    break;
                case RarityTier.Uncommon:
                    uncommon++;
                    break;
                default:
                    common++;
                    break;
            }
        }

        public static void AccumulateTrophy(AchievementDetail achievement, ref int platinum, ref int gold, ref int silver, ref int bronze)
        {
            AccumulateTrophy(achievement?.TrophyType, ref platinum, ref gold, ref silver, ref bronze);
        }

        public static void AccumulateTrophy(string trophyType, ref int platinum, ref int gold, ref int silver, ref int bronze)
        {
            if (string.IsNullOrWhiteSpace(trophyType))
            {
                return;
            }

            switch (trophyType.Trim().ToLowerInvariant())
            {
                case "platinum":
                    platinum++;
                    break;
                case "gold":
                    gold++;
                    break;
                case "silver":
                    silver++;
                    break;
                case "bronze":
                    bronze++;
                    break;
            }
        }

        private void SetSource(AchievementDetail source, bool notifyChanges)
        {
            if (ReferenceEquals(_source, source) && !notifyChanges)
            {
                return;
            }

            _source = source;
            if (notifyChanges)
            {
                NotifySourceChanged();
            }
        }

        private bool SetSourceValue<T>(
            Func<AchievementDetail, T> getter,
            Action<AchievementDetail, T> setter,
            T value,
            params string[] propertyNames)
        {
            var source = _source ?? (_source = new AchievementDetail());
            if (EqualityComparer<T>.Default.Equals(getter(source), value))
            {
                return false;
            }

            setter(source, value);
            for (var i = 0; i < propertyNames.Length; i++)
            {
                OnPropertyChanged(propertyNames[i]);
            }

            return true;
        }

        private void NotifySourceChanged()
        {
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(UnlockedIconPath));
            OnPropertyChanged(nameof(LockedIconPath));
            OnPropertyChanged(nameof(IconPath));
            OnPropertyChanged(nameof(UnlockTimeUtc));
            OnPropertyChanged(nameof(UnlockTimeText));
            OnPropertyChanged(nameof(DateUnlocked));
            OnPropertyChanged(nameof(UnlockTime));
            OnPropertyChanged(nameof(ShowUnlockDate));
            OnPropertyChanged(nameof(ShowLockedProgress));
            OnPropertyChanged(nameof(GlobalPercentUnlocked));
            OnPropertyChanged(nameof(HasRarityPercent));
            OnPropertyChanged(nameof(GlobalPercentText));
            OnPropertyChanged(nameof(RarityDetailText));
            OnPropertyChanged(nameof(GlobalPercent));
            OnPropertyChanged(nameof(RarityPercentValue));
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(RaritySortValue));
            OnPropertyChanged(nameof(CollectionScore));
            OnPropertyChanged(nameof(PrestigeScore));
            OnPropertyChanged(nameof(Rarity));
            OnPropertyChanged(nameof(GamerScore));
            OnPropertyChanged(nameof(Unlocked));
            OnPropertyChanged(nameof(ShowUnlockDate));
            OnPropertyChanged(nameof(ShowLockedProgress));
            OnPropertyChanged(nameof(IsCapstone));
            OnPropertyChanged(nameof(IsGoal));
            OnPropertyChanged(nameof(GoalOrderIndex));
            OnPropertyChanged(nameof(RarityBrush));
            OnPropertyChanged(nameof(RarityNameBrush));
            OnPropertyChanged(nameof(Hidden));
            OnPropertyChanged(nameof(IsUnlock));
            OnPropertyChanged(nameof(ApiName));
            OnPropertyChanged(nameof(AchievementNote));
            OnPropertyChanged(nameof(HasAchievementNote));
            OnPropertyChanged(nameof(AchievementNoteInlineMarker));
            OnPropertyChanged(nameof(ProgressNum));
            OnPropertyChanged(nameof(ProgressDenom));
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(TrophyType));
            OnPropertyChanged(nameof(HasTrophyType));
            NotifyRevealStateChanged();
            OnPropertyChanged(nameof(IsIconHidden));
            OnPropertyChanged(nameof(IsLockedIconHidden));
            OnPropertyChanged(nameof(IsTitleHidden));
            OnPropertyChanged(nameof(IsDescriptionHidden));
            NotifyTitleDisplayChanged();
            NotifyDescriptionDisplayChanged();
            NotifyIconPathChanged();
        }

        private void NotifyIconPathChanged()
        {
            OnPropertyChanged(nameof(IconPath));
            NotifyIconDisplayChanged();
            OnPropertyChanged(nameof(ImageUnlocked));
            OnPropertyChanged(nameof(ImageLocked));
            OnPropertyChanged(nameof(UsesExplicitLockedIcon));
        }

        private void NotifyRevealStateChanged()
        {
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(IsHidden));
        }

        private void NotifyTitleDisplayChanged()
        {
            OnPropertyChanged(nameof(DisplayNameResolved));
            OnPropertyChanged(nameof(HiddenTitleSuffix));
        }

        private void NotifyDescriptionDisplayChanged()
        {
            OnPropertyChanged(nameof(DescriptionResolved));
            OnPropertyChanged(nameof(ApiNameResolved));
        }

        private void NotifyIconDisplayChanged()
        {
            OnPropertyChanged(nameof(DisplayIcon));
            OnPropertyChanged(nameof(Icon));
        }

        private bool ShouldShowPlaceholderIcon()
        {
            return (IsHidden && Hidden && !ShowHiddenIcon) ||
                   (!UnlockedForVisibility && !ShowLockedIcon && !IsRevealed);
        }

        private string GetLockedDisplayIcon()
        {
            return AchievementIconResolver.GetLockedDisplayIcon(
                UnlockedIconPath,
                LockedIconPath);
        }

        private static AchievementDisplayItem CreateBaseItem(
            GameAchievementData gameData,
            AchievementDetail achievement,
            Guid? playniteGameId,
            int? pointsValue,
            CategoryPresentationMemo categoryMemo = null)
        {
            var item = new AchievementDisplayItem();
            item.SetSource(achievement, notifyChanges: false);
            item.ProviderKey = achievement.ProviderKey ?? gameData?.EffectiveProviderKey ?? gameData?.ProviderKey;
            item.GameName = gameData?.GameName ?? "Unknown";
            item.SortingName = gameData?.SortingName ?? gameData?.GameName ?? "Unknown";
            item.PlayniteGameId = playniteGameId;
            item.PointsValue = pointsValue;
            item.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(achievement.CategoryType);
            item.CategoryLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category);
            item.GameIconPath = ResolveGameAssetPath(gameData?.Game?.Icon);
            item.GameCoverPath = ResolveGameAssetPath(gameData?.Game?.CoverImage);
            ApplyCategoryPresentation(
                item,
                gameData,
                item.CategoryLabel,
                achievement.ProviderCategory,
                playniteGameId,
                categoryMemo);
            return item;
        }

        private static void ApplyCategoryPresentation(
            AchievementDisplayItem item,
            GameAchievementData gameData,
            string categoryLabel,
            string providerCategoryLabel,
            Guid? playniteGameId,
            CategoryPresentationMemo categoryMemo = null)
        {
            ApplyCategoryPresentation(
                item,
                gameData?.AchievementCategoryOrder,
                gameData?.AchievementCategoryImageOverrides,
                categoryLabel,
                providerCategoryLabel,
                playniteGameId,
                categoryMemo);
        }

        internal static void ApplyCategoryPresentation(
            AchievementDisplayItem item,
            IReadOnlyList<string> categoryOrder,
            IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            string categoryLabel,
            string providerCategoryLabel,
            Guid? playniteGameId,
            CategoryPresentationMemo categoryMemo = null)
        {
            if (item == null)
            {
                return;
            }

            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryLabel);

            // Default images are keyed by the provider label (renames only affect the
            // displayed label); fall back to the effective label when no provider label
            // is available, e.g. un-hydrated details where the two are identical.
            var providerCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(
                string.IsNullOrWhiteSpace(providerCategoryLabel) ? categoryLabel : providerCategoryLabel);

            string memoKey = null;
            if (categoryMemo != null)
            {
                memoKey = string.Concat(normalizedCategory, "\u001f", providerCategory);
                if (categoryMemo.TryGet(memoKey, out var cached))
                {
                    item.CategoryOrderIndex = cached.OrderIndex;
                    item.CategoryArtPath = cached.ArtPath;
                    return;
                }
            }

            var orderIndex = AchievementCategoryFilterOrderHelper.ResolveCategoryOrderIndex(normalizedCategory, categoryOrder);
            item.CategoryOrderIndex = orderIndex;

            CategoryImageOverrideData imageOverride = null;
            if (!string.IsNullOrWhiteSpace(normalizedCategory) &&
                categoryImageOverrides != null)
            {
                categoryImageOverrides.TryGetValue(normalizedCategory, out imageOverride);
            }

            // Default art is normally keyed by the provider label, but an achievement recategorized
            // into another category (e.g. via a category merge) keeps its original provider label
            // while its effective label now points at the target category. Probe the effective label
            // first so every achievement in the target category resolves the target's art (rather than
            // its old category's), then fall back to the provider label for un-merged categories,
            // including renames where the effective label has no default file of its own.
            var artPath =
                ResolveCategoryImageOverridePath(imageOverride?.Art, playniteGameId) ??
                CategoryDefaultImageResolver.Resolve(playniteGameId, normalizedCategory) ??
                CategoryDefaultImageResolver.Resolve(playniteGameId, providerCategory);
            item.CategoryArtPath = artPath;

            if (memoKey != null)
            {
                categoryMemo.Set(memoKey, new CategoryPresentationMemo.Entry
                {
                    OrderIndex = orderIndex,
                    ArtPath = artPath
                });
            }
        }

        private static string ResolveCategoryImageOverridePath(string value, Guid? playniteGameId)
        {
            var normalized = NormalizeImagePath(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var managedCustomIconService = PlayniteAchievementsPlugin.Instance?.ManagedCustomIconService;
            var resolved = playniteGameId.HasValue
                ? managedCustomIconService?.ResolveManagedDisplayPath(normalized, playniteGameId.Value.ToString("D")) ?? normalized
                : normalized;
            // Category graphics are overwritten in place at a stable managed path, so the
            // display path needs a cache-bust token or stale bitmaps are served after replacement.
            return AchievementIconResolver.ApplyCacheBust(resolved);
        }

        private static string ResolveGameAssetPath(string value)
        {
            var normalized = NormalizeImagePath(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            try
            {
                return API.Instance?.Database?.GetFullFilePath(normalized) ?? normalized;
            }
            catch
            {
                return normalized;
            }
        }

        private static string NormalizeImagePath(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static int? ResolvePoints(AchievementDetail achievement, GameAchievementData gameData)
        {
            if (achievement == null)
            {
                return null;
            }

            if (RetroAchievementsDataProvider.UseScaledPoints(gameData))
            {
                return achievement.ScaledPoints ?? achievement.Points;
            }

            return achievement.Points;
        }

        private static bool ShouldRestoreRevealedState(
            GameAchievementData gameData,
            AchievementDetail achievement,
            PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys,
            Guid? gameId)
        {
            if (achievement == null || !achievement.Hidden || achievement.Unlocked)
            {
                return false;
            }

            var persisted = settings?.Persisted;
            var hidesAny = !(persisted?.ShowHiddenIcon ?? false) ||
                           !(persisted?.ShowHiddenTitle ?? false) ||
                           !(persisted?.ShowHiddenDescription ?? false);
            if (!hidesAny || revealedKeys == null || revealedKeys.Count == 0)
            {
                return false;
            }

            var key = MakeRevealKey(gameId, achievement.ApiName, gameData?.GameName);
            return revealedKeys.Contains(key);
        }

        private static string NormalizePersistedPropertyName(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return string.Empty;
            }

            const string persistedPrefix = "Persisted.";
            return propertyName.StartsWith(persistedPrefix, StringComparison.Ordinal)
                ? propertyName.Substring(persistedPrefix.Length)
                : propertyName;
        }
    }
}
