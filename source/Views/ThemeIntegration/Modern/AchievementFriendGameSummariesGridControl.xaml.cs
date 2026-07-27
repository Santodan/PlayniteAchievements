using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.ThemeIntegration.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    public partial class AchievementFriendGameSummariesGridControl : ThemeControlBase
    {
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(
                nameof(DisplayItems),
                typeof(IEnumerable<GameSummaryItem>),
                typeof(AchievementFriendGameSummariesGridControl),
                new PropertyMetadata(null));

        public IEnumerable<GameSummaryItem> DisplayItems
        {
            get => (IEnumerable<GameSummaryItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        public static readonly DependencyProperty ColumnSettingsKeyProperty =
            DependencyProperty.Register(
                nameof(ColumnSettingsKey),
                typeof(string),
                typeof(AchievementFriendGameSummariesGridControl),
                new PropertyMetadata("FriendsOverviewGameSummaries"));

        public string ColumnSettingsKey
        {
            get => (string)GetValue(ColumnSettingsKeyProperty);
            private set => SetValue(ColumnSettingsKeyProperty, value);
        }

        public AchievementFriendGameSummariesGridControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesThemeBindings => true;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadData();
        }

        protected override void OnThemeDataUpdated()
        {
            LoadData();
        }

        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return string.IsNullOrWhiteSpace(propertyName) ||
                   propertyName == nameof(ModernThemeBindings.DynamicFriendGameSummaries) ||
                   propertyName == nameof(ModernThemeBindings.DynamicFriendScopeUserKey);
        }

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.ShowFriendsOverviewGameSummariesGridColumnHeaders) ||
                   propertyName == nameof(PersistedSettings.FriendsOverviewGameSummariesGridRowHeight);
        }

        private void LoadData()
        {
            var theme = EffectiveTheme;
            DisplayItems = ProjectDisplayItems(theme?.DynamicFriendGameSummaries);
            ColumnSettingsKey = FriendOverviewProjection.IsAllScope(theme?.DynamicFriendScopeUserKey)
                ? "FriendsOverviewGameSummaries"
                : "FriendsOverviewSelectedFriendGameSummaries";
        }

        private static List<GameSummaryItem> ProjectDisplayItems(IEnumerable<FriendGameAchievementSummary> summaries)
        {
            return (summaries ?? Enumerable.Empty<FriendGameAchievementSummary>())
                .Where(summary => summary != null)
                .Select(ProjectDisplayItem)
                .Cast<GameSummaryItem>()
                .ToList();
        }

        private static FriendGameSummaryItem ProjectDisplayItem(FriendGameAchievementSummary summary)
        {
            var gameId = summary.GameId != Guid.Empty ? summary.GameId : (Guid?)null;
            // summary.ProviderKey already carries the resolved display-provider key (e.g. Steam), so
            // resolve the brand icon/color from it rather than defaulting to gray.
            if (!PlayniteAchievements.Providers.ProviderRegistry.TryResolveProviderVisuals(summary.ProviderKey, out var providerIconKey, out var providerColorHex))
            {
                providerIconKey = string.IsNullOrWhiteSpace(summary.ProviderKey) ? null : "ProviderIcon" + summary.ProviderKey;
                providerColorHex = "#888888";
            }
            return new FriendGameSummaryItem
            {
                ProviderKey = summary.ProviderKey,
                Provider = !string.IsNullOrWhiteSpace(summary.ProviderName) ? summary.ProviderName : summary.Platform,
                ProviderIconKey = providerIconKey,
                ProviderColorHex = providerColorHex,
                AppId = summary.AppId,
                PlayniteGameId = gameId,
                GameName = summary.GameName,
                SortingName = summary.SortingName,
                GameCoverPath = summary.CoverImagePath,
                PlatformText = summary.Platform,
                LastPlayed = summary.LastFriendPlayedUtc ?? summary.LastPlayed,
                PlaytimeSeconds = ToPlaytimeSeconds(summary.TotalFriendPlaytimeMinutes),
                TotalAchievements = summary.AchievementCount,
                UnlockedAchievements = summary.UniqueFriendUnlockedAchievementsCount,
                CollectionScore = summary.CollectionScore,
                CollectionScoreTotal = summary.CollectionScoreTotal,
                PrestigeScore = summary.PrestigeScore,
                PrestigeScoreTotal = summary.PrestigeScoreTotal,
                Points = summary.Points,
                CommonCount = summary.Common?.Unlocked ?? 0,
                TotalCommonPossible = summary.Common?.Total ?? 0,
                UncommonCount = summary.Uncommon?.Unlocked ?? 0,
                TotalUncommonPossible = summary.Uncommon?.Total ?? 0,
                RareCount = summary.Rare?.Unlocked ?? 0,
                TotalRarePossible = summary.Rare?.Total ?? 0,
                UltraRareCount = summary.UltraRare?.Unlocked ?? 0,
                TotalUltraRarePossible = summary.UltraRare?.Total ?? 0,
                TrophyPlatinumCount = summary.TrophyPlatinumCount,
                TrophyGoldCount = summary.TrophyGoldCount,
                TrophySilverCount = summary.TrophySilverCount,
                TrophyBronzeCount = summary.TrophyBronzeCount,
                TrophyPlatinumTotal = summary.TrophyPlatinumTotal,
                TrophyGoldTotal = summary.TrophyGoldTotal,
                TrophySilverTotal = summary.TrophySilverTotal,
                TrophyBronzeTotal = summary.TrophyBronzeTotal,
                IsCompleted = summary.IsCompleted,
                LastFriendUnlockUtc = summary.LastFriendUnlockUtc,
                FriendCount = summary.FriendCount,
                FriendsWithUnlocksCount = summary.FriendsWithUnlocksCount,
                FriendUnlockedAchievementsCount = summary.FriendUnlockedAchievementsCount,
                UniqueFriendUnlockedAchievementsCount = summary.UniqueFriendUnlockedAchievementsCount,
                TotalFriendPlaytimeMinutes = summary.TotalFriendPlaytimeMinutes,
                AverageFriendPlaytimeMinutes = summary.AverageFriendPlaytimeMinutes,
                LastFriendPlayedUtc = summary.LastFriendPlayedUtc,
                LastFriendScrapedUtc = summary.LastFriendScrapedUtc,
                LastFriendScrapeStatus = summary.LastFriendScrapeStatus,
                SetDynamicAchievementsGameCommand = summary.SetDynamicAchievementsGameCommand,
                FilterDynamicGameSummariesByProviderCommand = summary.FilterDynamicGameSummariesByProviderCommand,
                OpenViewAchievementsWindow = summary.OpenViewAchievementsWindow,
                OpenManageAchievementsWindow = summary.OpenManageAchievementsWindow,
                SetDynamicFriendScopeProviderCommand = summary.SetDynamicFriendScopeProviderCommand,
                SetDynamicFriendScopeGameCommand = summary.SetDynamicFriendScopeGameCommand
            };
        }

        private static ulong ToPlaytimeSeconds(long minutes)
        {
            var normalized = Math.Max(0L, minutes);
            return normalized > (long)(ulong.MaxValue / 60UL)
                ? ulong.MaxValue
                : (ulong)normalized * 60UL;
        }
    }
}
