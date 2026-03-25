using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements
{
    public class PlayniteAchievementsPlugin
    {
        public static PlayniteAchievementsPlugin Instance { get; set; }

        public PlayniteAchievementsSettings Settings { get; set; }

        public IPlayniteAPI PlayniteApi { get; set; }
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

        public DateTime? UnlockTimeUtc { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        public int? Points { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public string TrophyType { get; set; }

        public string CategoryType { get; set; }

        public string Category { get; set; }

        public string ProviderKey { get; set; }

        public Game Game { get; set; }

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

        public Guid? PlayniteGameId { get; set; }

        public Game Game { get; set; }

        public List<AchievementDetail> Achievements { get; set; } = new List<AchievementDetail>();

        public bool IsCompleted =>
            (Achievements?.Count > 0 && Achievements.All(a => a?.Unlocked == true)) ||
            (Achievements?.Any(a => a?.IsCapstone == true && a.Unlocked) == true);
    }
}

namespace PlayniteAchievements.Services
{
    public static class AchievementProjectionService
    {
        public static bool IsAppearanceSettingPropertyName(string propertyName)
        {
            return false;
        }
    }

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

        public void CloseOverlayWindowIfOpen()
        {
        }
    }
}

namespace PlayniteAchievements.ViewModels
{
    public class AchievementDisplayItem
    {
        public bool ShowHiddenSuffix { get; set; }

        public void UpdateFrom(
            PlayniteAchievements.Models.Achievements.AchievementDetail source,
            string gameName,
            Guid? playniteGameId,
            bool hideIcon,
            bool hideTitle,
            bool hideDescription,
            bool hideLockedIcon,
            bool showRarityGlow,
            bool showRarityBar)
        {
        }
    }
}
