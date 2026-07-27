using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements
{
    public class PlayniteAchievementsPlugin
    {
        public static PlayniteAchievementsPlugin Instance { get; set; }

        public static event EventHandler SettingsSaved;

        public static void NotifySettingsSaved() => SettingsSaved?.Invoke(null, EventArgs.Empty);

        public PlayniteAchievementsSettings Settings { get; set; }

        public Services.Achievements.AchievementDataService AchievementDataService { get; set; }

        public Services.GameCustomData.GameCustomDataStore GameCustomDataStore { get; set; }

        public Services.ThemeIntegration.ThemeIntegrationService ThemeIntegrationService { get; set; }

        public IPlayniteAPI PlayniteApi { get; set; }

        public void SavePluginSettings(PlayniteAchievementsSettings settings)
        {
            Settings = settings;
        }

        public void RequestThemeUpdate(Game gameContext)
        {
        }

        public void OpenViewAchievementsWindow(Guid gameId, string focusAchievementId = null)
        {
        }
    }
}

namespace PlayniteAchievements.Models.Achievements
{
    public enum RarityTier
    {
        Common,
        Uncommon,
        Rare,
        UltraRare
    }

    public static class PercentRarityHelper
    {
        private static double _ultraRareThreshold = 5;
        private static double _rareThreshold = 10;
        private static double _uncommonThreshold = 50;

        public static void Configure(double ultraRare, double rare, double uncommon)
        {
            _ultraRareThreshold = ultraRare;
            _rareThreshold = rare;
            _uncommonThreshold = uncommon;
        }

        public static RarityTier GetRarityTier(double globalPercent)
        {
            if (globalPercent <= _ultraRareThreshold) return RarityTier.UltraRare;
            if (globalPercent <= _rareThreshold) return RarityTier.Rare;
            if (globalPercent <= _uncommonThreshold) return RarityTier.Uncommon;
            return RarityTier.Common;
        }
    }

    public sealed class AchievementDetail
    {
        public string ApiName { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public bool Hidden { get; set; }

        public bool Unlocked { get; set; }

        public bool IsCapstone { get; set; }

        public bool IsFiltered { get; set; }

        public bool IsFilteredFromSummaries { get; set; }

        public string AchievementNote { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        public int? Points { get; set; }

        public int? ScaledPoints { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public string TrophyType { get; set; }

        public string CategoryType { get; set; }

        public string Category { get; set; }

        public string ProviderCategory { get; set; }

        public string ProviderKey { get; set; }

        public Game Game { get; set; }

        public string CategoryArtPath { get; set; }

        public int CategoryOrderIndex { get; set; } = int.MaxValue;

        public System.Windows.Input.ICommand SetDynamicAchievementsGameCommand { get; set; }

        public System.Windows.Input.ICommand FilterDynamicLibraryAchievementsByProviderCommand { get; set; }

        public System.Windows.Input.ICommand OpenViewAchievementsWindow { get; set; }

        public System.Windows.Input.ICommand OpenManageAchievementsWindow { get; set; }

        public bool HasRarityPercent => GlobalPercentUnlocked.HasValue;

        public RarityTier Rarity { get; set; } = RarityTier.Common;

        public double RaritySortValue
        {
            get
            {
                var band = Rarity switch
                {
                    RarityTier.UltraRare => 0,
                    RarityTier.Rare => 1_000_000,
                    RarityTier.Uncommon => 2_000_000,
                    _ => 3_000_000
                };

                return GlobalPercentUnlocked.HasValue
                    ? band + Math.Round(GlobalPercentUnlocked.Value * 1000, MidpointRounding.AwayFromZero)
                    : band + 999_999;
            }
        }

        public int CollectionScore =>
            PlayniteAchievements.Models.Achievements.Scoring.AchievementScoreCalculator.GetCollectionValue(Rarity);

        public int PrestigeScore =>
            PlayniteAchievements.Models.Achievements.Scoring.AchievementScoreCalculator.GetPrestigeValue(GlobalPercentUnlocked, Rarity);
    }

    public sealed class GameAchievementData
    {
        public DateTime LastUpdatedUtc { get; set; }

        public string ProviderKey { get; set; }

        public string ProviderPlatformKey { get; set; }

        public string EffectiveProviderKey =>
            !string.IsNullOrWhiteSpace(ProviderPlatformKey)
                ? ProviderPlatformKey
                : ProviderKey;

        public string LibrarySourceName { get; set; }

        public bool HasAchievements { get; set; } = true;

        public string GameName { get; set; }

        public int AppId { get; set; }

        public string ProviderGameKey { get; set; }

        public Guid? PlayniteGameId { get; set; }

        public Game Game { get; set; }

        public List<string> AchievementOrder { get; set; }

        public List<string> AchievementCategoryOrder { get; set; }

        public Dictionary<string, PlayniteAchievements.Models.Settings.CategoryImageOverrideData> AchievementCategoryImageOverrides { get; set; }

        public PlayniteAchievements.Models.Settings.GameSummaryCategoryData GameSummaryCategory { get; set; }

        public List<AchievementDetail> Achievements { get; set; } = new List<AchievementDetail>();

        public bool IsCompleted =>
            (Achievements?.Count > 0 && Achievements.All(a => a?.Unlocked == true)) ||
            (Achievements?.Any(a => a?.IsCapstone == true && a.Unlocked) == true);
    }
}

namespace PlayniteAchievements.Services
{
    public class ProviderRegistry
    {
        public static string GetLocalizedName(string providerKey)
        {
            return providerKey ?? string.Empty;
        }

        public virtual Task PrimeEnabledProvidersAsync()
        {
            return Task.CompletedTask;
        }

        public static T Settings<T>() where T : Providers.Settings.ProviderSettingsBase, new()
        {
            return Providers.Settings.ProviderSettingsHelper.LoadCurrent<T>();
        }

        public static void Write(Providers.Settings.ProviderSettingsBase settings, bool persistToDisk = false)
        {
            Providers.Settings.ProviderSettingsHelper.SaveCurrent(settings);
        }
    }
}

namespace PlayniteAchievements.Services.ThemeIntegration
{
    public sealed class FullscreenWindowService : IDisposable
    {
        public FullscreenWindowService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            Action<Guid?> requestSingleGameThemeUpdate)
        {
        }

        public bool IsOverlayWindowOpen => false;

        public void Dispose()
        {
        }

        public void OpenOverviewWindow()
        {
        }

        public void OpenGameWindow(Guid gameId)
        {
        }

        public void OpenViewAchievementsWindow(Guid gameId)
        {
        }

        public void CloseOverlayWindowIfOpen()
        {
        }
    }
}

namespace PlayniteAchievements.ViewModels
{
    public class AchievementDisplayItem
    {
        public sealed class AppearanceSettingsSnapshot
        {
        }

        public PlayniteAchievements.Models.Achievements.AchievementDetail Source { get; set; }

        public string DisplayName { get; set; }

        public string Name => DisplayName;

        public string Description { get; set; }

        public string SortingName { get; set; }

        public string GameName { get; set; }

        public string ProviderKey { get; set; }

        public string FriendName { get; set; }

        public string FriendExternalUserId { get; set; }

        public string FriendAvatarPath { get; set; }

        public Guid? PlayniteGameId { get; set; }

        public System.Windows.Input.ICommand SetDynamicAchievementsGameCommand { get; set; }

        public System.Windows.Input.ICommand FilterDynamicLibraryAchievementsByProviderCommand { get; set; }

        public System.Windows.Input.ICommand OpenViewAchievementsWindow { get; set; }

        public System.Windows.Input.ICommand OpenManageAchievementsWindow { get; set; }

        public string ApiName { get; set; }

        public string TrophyType { get; set; }

        public string CategoryType { get; set; }

        public string CategoryLabel { get; set; }

        public int CategoryOrderIndex { get; set; } = int.MaxValue;

        public string CategoryArtPath { get; set; }

        public string GameIconPath { get; set; }

        public string GameCoverPath { get; set; }

        public string AchievementNote { get; set; }

        public bool HasAchievementNote => !string.IsNullOrWhiteSpace(AchievementNote);

        public bool IsCapstone { get; set; }

        public bool Hidden { get; set; }

        public bool Unlocked { get; set; }

        public virtual bool ShowUnlockDate => Unlocked;

        public virtual bool ShowLockedProgress => !ShowUnlockDate;

        public DateTime? UnlockTimeUtc { get; set; }

        public DateTime UnlockTime => UnlockTimeUtc ?? DateTime.MinValue;

        public double? GlobalPercentUnlocked { get; set; }

        public double GlobalPercent => GlobalPercentUnlocked ?? 0;

        public PlayniteAchievements.Models.Achievements.RarityTier Rarity { get; set; }
            = PlayniteAchievements.Models.Achievements.RarityTier.Common;

        public double RaritySortValue { get; set; }

        public int CollectionScore { get; set; }

        public int PrestigeScore { get; set; }

        public int? PointsValue { get; set; }

        public int Points => PointsValue ?? 0;

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public bool HasProgress => ProgressNum.HasValue && ProgressDenom.HasValue && ProgressDenom.Value > 0;

        public double ProgressPercent =>
            HasProgress
                ? ProgressNum.Value * 100.0 / ProgressDenom.Value
                : 0;

        public bool ShowHiddenSuffix { get; set; }

        public bool ShowHiddenIcon { get; set; }

        public bool ShowHiddenTitle { get; set; }

        public bool ShowHiddenDescription { get; set; }

        public bool ShowLockedIcon { get; set; }

        public bool UseSeparateLockedIconsWhenAvailable { get; set; }

        public bool ShowRarityBar { get; set; } = true;

        public bool ShowFriendSpoilers { get; set; }

        public virtual bool UnlockedForVisibility => Unlocked;

        public bool IsRevealed { get; set; }

        public static AchievementDisplayItem Create(
            PlayniteAchievements.Models.Achievements.GameAchievementData gameData,
            PlayniteAchievements.Models.Achievements.AchievementDetail achievement,
            PlayniteAchievements.Models.PlayniteAchievementsSettings settings,
            ISet<string> revealedKeys = null,
            Guid? playniteGameIdOverride = null,
            AppearanceSettingsSnapshot appearanceSettings = null)
        {
            return new AchievementDisplayItem();
        }

        public static AchievementDisplayItem CreateRecent(
            PlayniteAchievements.Models.Achievements.GameAchievementData gameData,
            PlayniteAchievements.Models.Achievements.AchievementDetail achievement,
            PlayniteAchievements.Models.PlayniteAchievementsSettings settings,
            string gameIconPath,
            string gameCoverPath,
            AppearanceSettingsSnapshot appearanceSettings = null)
        {
            return new AchievementDisplayItem();
        }

        public static AppearanceSettingsSnapshot CreateAppearanceSettingsSnapshot(
            PlayniteAchievements.Models.PlayniteAchievementsSettings settings,
            Guid? playniteGameId,
            bool? resolvedUseSeparateLockedIcons)
        {
            return new AppearanceSettingsSnapshot();
        }

        public static bool IsAppearanceSettingPropertyName(string propertyName)
        {
            return false;
        }

        public static string MakeRevealKey(Guid? playniteGameId, string apiName, string gameName)
        {
            var gamePart = playniteGameId?.ToString() ?? (gameName ?? string.Empty);
            return $"{gamePart}\u001f{apiName ?? string.Empty}";
        }

        public static void AccumulateRarity(
            PlayniteAchievements.Models.Achievements.AchievementDetail achievement,
            ref int common,
            ref int uncommon,
            ref int rare,
            ref int ultraRare)
        {
        }

        public static void AccumulateTrophy(
            PlayniteAchievements.Models.Achievements.AchievementDetail achievement,
            ref int platinum,
            ref int gold,
            ref int silver,
            ref int bronze)
        {
        }

        public static void AccumulateTrophy(
            string trophyType,
            ref int platinum,
            ref int gold,
            ref int silver,
            ref int bronze)
        {
        }

        public void ApplyAppearanceSettings(PlayniteAchievements.Models.PlayniteAchievementsSettings settings)
        {
        }

        public void ApplyAppearanceSettings(AppearanceSettingsSnapshot snapshot)
        {
        }

        public AchievementDisplayItem Clone()
        {
            return new AchievementDisplayItem
            {
                DisplayName = DisplayName,
                Description = Description,
                SortingName = SortingName,
                GameName = GameName,
                ProviderKey = ProviderKey,
                PlayniteGameId = PlayniteGameId,
                ApiName = ApiName,
                TrophyType = TrophyType,
                CategoryType = CategoryType,
                CategoryLabel = CategoryLabel,
                CategoryOrderIndex = CategoryOrderIndex,
                CategoryArtPath = CategoryArtPath,
                GameIconPath = GameIconPath,
                GameCoverPath = GameCoverPath,
                Hidden = Hidden,
                IsCapstone = IsCapstone,
                Unlocked = Unlocked,
                UnlockTimeUtc = UnlockTimeUtc,
                GlobalPercentUnlocked = GlobalPercentUnlocked,
                Rarity = Rarity,
                RaritySortValue = RaritySortValue,
                CollectionScore = CollectionScore,
                PrestigeScore = PrestigeScore,
                PointsValue = PointsValue,
                ProgressNum = ProgressNum,
                ProgressDenom = ProgressDenom,
                AchievementNote = AchievementNote,
                ShowHiddenSuffix = ShowHiddenSuffix,
                ShowHiddenIcon = ShowHiddenIcon,
                ShowHiddenTitle = ShowHiddenTitle,
                ShowHiddenDescription = ShowHiddenDescription,
                ShowLockedIcon = ShowLockedIcon,
                UseSeparateLockedIconsWhenAvailable = UseSeparateLockedIconsWhenAvailable,
                ShowRarityBar = ShowRarityBar,
                IsRevealed = IsRevealed,
                Source = Source
            };
        }

        public void UpdateFrom(
            PlayniteAchievements.Models.Achievements.AchievementDetail source,
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
            Source = source;
            DisplayName = source?.DisplayName;
            Description = source?.Description;
            GameName = gameName;
            SortingName = sortingName ?? gameName;
            ProviderKey = source?.ProviderKey;
            PlayniteGameId = playniteGameId;
            ApiName = source?.ApiName;
            TrophyType = source?.TrophyType;
            CategoryType = source?.CategoryType;
            CategoryLabel = source?.Category;
            Hidden = source?.Hidden == true;
            IsCapstone = source?.IsCapstone == true;
            Unlocked = source?.Unlocked == true;
            UnlockTimeUtc = source?.UnlockTimeUtc;
            GlobalPercentUnlocked = source?.GlobalPercentUnlocked;
            Rarity = source?.Rarity ?? PlayniteAchievements.Models.Achievements.RarityTier.Common;
            RaritySortValue = source?.RaritySortValue ?? 0;
            CollectionScore = source?.CollectionScore ?? 0;
            PrestigeScore = source?.PrestigeScore ?? 0;
            PointsValue = source?.Points;
            ProgressNum = source?.ProgressNum;
            ProgressDenom = source?.ProgressDenom;
            AchievementNote = source?.AchievementNote;
            ShowHiddenIcon = showHiddenIcon;
            ShowHiddenTitle = showHiddenTitle;
            ShowHiddenDescription = showHiddenDescription;
            ShowHiddenSuffix = showHiddenSuffix;
            ShowLockedIcon = showLockedIcon;
            UseSeparateLockedIconsWhenAvailable = useSeparateLockedIconsWhenAvailable;
            ShowRarityBar = showRarityBar;
            GameIconPath = gameIconPath;
            GameCoverPath = gameCoverPath;
            CategoryOrderIndex = categoryOrderIndex;
            // Mirrors the real display item: no game-asset fallback baked into the art path.
            CategoryArtPath = categoryArtPath ?? source?.CategoryArtPath;
        }
    }
}
