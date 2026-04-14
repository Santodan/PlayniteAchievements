using System;
using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Services;
using Playnite.SDK;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
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

            public bool ShowRarityGlow { get; set; }

            public bool ShowRarityBar { get; set; }
        }

        private AchievementDetail _source;
        private string _gameName;
        private string _sortingName;
        private Guid? _playniteGameId;
        private string _providerKey;
        private int? _pointsValue;
        private string _categoryType;
        private string _categoryLabel;
        private string _gameIconPath;
        private string _gameCoverPath;

        public AchievementDetail Source => _source;

        public string GameName { get => _gameName; set => SetValue(ref _gameName, value); }

        public string SortingName { get => _sortingName; set => SetValue(ref _sortingName, value); }

        public Guid? PlayniteGameId { get => _playniteGameId; set => SetValue(ref _playniteGameId, value); }

        public string ProviderKey
        {
            get => _providerKey;
            set => SetValue(ref _providerKey, value);
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
                    OnPropertyChanged(nameof(DateUnlocked));
                    OnPropertyChanged(nameof(UnlockTime));
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
                    OnPropertyChanged(nameof(IsUnlock));
                }
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
                }
            }
        }

        public string CategoryTypeDisplay => AchievementCategoryTypeHelper.ToDisplayText(CategoryType);

        public string CategoryLabel
        {
            get => _categoryLabel;
            set => SetValue(ref _categoryLabel, value);
        }

        /// <summary>
        /// Path to the game's icon image.
        /// Used by the Game column in sidebar recent achievements.
        /// </summary>
        public string GameIconPath
        {
            get => _gameIconPath;
            set => SetValue(ref _gameIconPath, value);
        }

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
            bool showRarityGlow,
            bool showRarityBar = true,
            string sortingName = null,
            string gameIconPath = null,
            string gameCoverPath = null)
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
                showRarityGlow,
                showRarityBar);
            PointsValue = source?.Points;
            CategoryType = source?.CategoryType;
            CategoryLabel = source?.Category;
            GameIconPath = gameIconPath;
            GameCoverPath = gameCoverPath;
        }

        public void ApplyAppearanceSettings(
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon,
            bool useSeparateLockedIconsWhenAvailable,
            bool showRarityGlow,
            bool showRarityBar = true)
        {
            ShowHiddenIcon = showHiddenIcon;
            ShowHiddenTitle = showHiddenTitle;
            ShowHiddenDescription = showHiddenDescription;
            ShowHiddenSuffix = showHiddenSuffix;
            ShowLockedIcon = showLockedIcon;
            UseSeparateLockedIconsWhenAvailable = useSeparateLockedIconsWhenAvailable;
            ShowRarityGlow = showRarityGlow;
            ShowRarityBar = showRarityBar;
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
                resolved.ShowRarityGlow,
                resolved.ShowRarityBar);
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
                ShowRarityGlow = persisted?.ShowRarityGlow ?? true,
                ShowRarityBar = persisted?.ShowCompactListRarityBar ?? true
            };
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
            clone.ShowRarityGlow = _showRarityGlow;
            clone.ShowRarityBar = _showRarityBar;
            clone.CategoryType = _categoryType;
            clone.CategoryLabel = _categoryLabel;
            clone.GameIconPath = _gameIconPath;
            clone.GameCoverPath = _gameCoverPath;
            return clone;
        }

        public static AchievementDisplayItem Create(
            GameAchievementData gameData,
            AchievementDetail achievement,
            PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys = null,
            Guid? playniteGameIdOverride = null,
            AppearanceSettingsSnapshot appearanceSettings = null)
        {
            if (achievement == null)
            {
                return null;
            }

            var gameId = playniteGameIdOverride ?? gameData?.PlayniteGameId;
            var item = CreateBaseItem(gameData, achievement, gameId, ResolvePoints(achievement, gameData));
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
                case nameof(PersistedSettings.SeparateLockedIconEnabledGameIds):
                case nameof(PersistedSettings.ShowRarityGlow):
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
            OnPropertyChanged(nameof(GlobalPercentUnlocked));
            OnPropertyChanged(nameof(HasRarityPercent));
            OnPropertyChanged(nameof(GlobalPercentText));
            OnPropertyChanged(nameof(RarityDetailText));
            OnPropertyChanged(nameof(GlobalPercent));
            OnPropertyChanged(nameof(RarityPercentValue));
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(RaritySortValue));
            OnPropertyChanged(nameof(Rarity));
            OnPropertyChanged(nameof(GamerScore));
            OnPropertyChanged(nameof(Unlocked));
            OnPropertyChanged(nameof(Hidden));
            OnPropertyChanged(nameof(IsUnlock));
            OnPropertyChanged(nameof(ApiName));
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
                   (!Unlocked && !ShowLockedIcon && !IsRevealed);
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
            int? pointsValue)
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
            return item;
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
