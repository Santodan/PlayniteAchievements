using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Base class for desktop theme controls that display single-game achievement data.
    /// Gets data directly from AchievementService instead of ThemeData intermediate layer.
    /// </summary>
    public abstract class SingleGameDataControlBase : ThemeControlBase, INotifyPropertyChanged
    {
        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;

        #region Public Properties

        /// <summary>
        /// Gets the total number of achievements for the current game.
        /// </summary>
        public int AchievementCount { get; private set; }

        /// <summary>
        /// Gets the number of unlocked achievements for the current game.
        /// </summary>
        public int UnlockedCount { get; private set; }

        /// <summary>
        /// Gets the number of locked achievements for the current game.
        /// </summary>
        public int LockedCount { get; private set; }

        /// <summary>
        /// Gets the completion percentage (0-100) for the current game.
        /// </summary>
        public double ProgressPercentage { get; private set; }

        /// <summary>
        /// Gets a value indicating whether all achievements are unlocked.
        /// </summary>
        public bool IsCompleted { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the game has achievements.
        /// </summary>
        public bool HasAchievements { get; private set; }

        /// <summary>
        /// Gets common achievement statistics.
        /// </summary>
        public AchievementRarityStats Common { get; private set; } = new AchievementRarityStats();

        /// <summary>
        /// Gets uncommon achievement statistics.
        /// </summary>
        public AchievementRarityStats Uncommon { get; private set; } = new AchievementRarityStats();

        /// <summary>
        /// Gets rare achievement statistics.
        /// </summary>
        public AchievementRarityStats Rare { get; private set; } = new AchievementRarityStats();

        /// <summary>
        /// Gets ultra rare achievement statistics.
        /// </summary>
        public AchievementRarityStats UltraRare { get; private set; } = new AchievementRarityStats();

        /// <summary>
        /// Gets all achievements for the current game.
        /// </summary>
        public List<AchievementDetail> AllAchievements { get; private set; } = new List<AchievementDetail>();

        /// <summary>
        /// Gets all achievements as display items for the current game.
        /// </summary>
        public List<AchievementDisplayItem> AllAchievementDisplayItems { get; private set; } = new List<AchievementDisplayItem>();

        #endregion

        private bool _isLoaded;
        private bool _isSubscribedToCacheEvents;

        protected SingleGameDataControlBase()
        {
            DataContext = this;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            SubscribeToCacheEvents();
            LoadData();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            UnsubscribeFromCacheEvents();
        }

        private void SubscribeToCacheEvents()
        {
            if (_isSubscribedToCacheEvents || Plugin?.AchievementService == null)
                return;

            Plugin.AchievementService.GameCacheUpdated += OnGameCacheUpdated;
            Plugin.AchievementService.CacheDeltaUpdated += OnCacheDeltaUpdated;
            _isSubscribedToCacheEvents = true;
        }

        private void UnsubscribeFromCacheEvents()
        {
            if (!_isSubscribedToCacheEvents || Plugin?.AchievementService == null)
                return;

            Plugin.AchievementService.GameCacheUpdated -= OnGameCacheUpdated;
            Plugin.AchievementService.CacheDeltaUpdated -= OnCacheDeltaUpdated;
            _isSubscribedToCacheEvents = false;
        }

        private void OnGameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            if (!_isLoaded) return;

            var game = Plugin?.Settings?.SelectedGame;
            if (game == null) return;

            if (e.GameId != null && Guid.TryParse(e.GameId, out var gameId) && gameId == game.Id)
            {
                Dispatcher.BeginInvoke(new Action(LoadData));
            }
        }

        private void OnCacheDeltaUpdated(object sender, Models.CacheDeltaEventArgs e)
        {
            if (!_isLoaded) return;

            var game = Plugin?.Settings?.SelectedGame;
            if (game == null) return;

            if (e.Key != null && Guid.TryParse(e.Key, out var gameId) && gameId == game.Id)
            {
                Dispatcher.BeginInvoke(new Action(LoadData));
            }
        }

        /// <summary>
        /// Loads data from AchievementService and computes all statistics.
        /// </summary>
        protected virtual void LoadData()
        {
            var game = Plugin?.Settings?.SelectedGame;
            if (game == null)
            {
                ClearData();
                return;
            }

            var gameData = Plugin?.AchievementService?.GetGameAchievementData(game.Id);
            if (gameData == null || !gameData.HasAchievements)
            {
                ClearData();
                return;
            }

            HasAchievements = true;
            var options = AchievementProjectionService.CreateOptions(Plugin.Settings, gameData);

            // Build achievement list
            AllAchievements = gameData.Achievements?.ToList() ?? new List<AchievementDetail>();

            // Build display items
            var displayItems = new List<AchievementDisplayItem>();
            foreach (var ach in AllAchievements)
            {
                var item = AchievementProjectionService.CreateDisplayItem(gameData, ach, options, game.Id);
                if (item != null)
                    displayItems.Add(item);
            }
            AllAchievementDisplayItems = displayItems;

            // Compute counts
            AchievementCount = AllAchievements.Count;
            UnlockedCount = AllAchievements.Count(a => a.Unlocked);
            LockedCount = AchievementCount - UnlockedCount;

            // Compute percentage
            ProgressPercentage = AchievementCount > 0
                ? Math.Round((double)UnlockedCount / AchievementCount * 100, 1)
                : 0;

            // Compute completion
            IsCompleted = gameData.IsCompleted;

            // Compute rarity stats
            ComputeRarityStats(AllAchievements);

            // Notify all properties changed
            OnPropertyChanged(nameof(AchievementCount));
            OnPropertyChanged(nameof(UnlockedCount));
            OnPropertyChanged(nameof(LockedCount));
            OnPropertyChanged(nameof(ProgressPercentage));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(HasAchievements));
            OnPropertyChanged(nameof(Common));
            OnPropertyChanged(nameof(Uncommon));
            OnPropertyChanged(nameof(Rare));
            OnPropertyChanged(nameof(UltraRare));
            OnPropertyChanged(nameof(AllAchievements));
            OnPropertyChanged(nameof(AllAchievementDisplayItems));

            // Call derived class refresh
            OnDataLoaded();
        }

        private void ComputeRarityStats(List<AchievementDetail> achievements)
        {
            var common = new AchievementRarityStats();
            var uncommon = new AchievementRarityStats();
            var rare = new AchievementRarityStats();
            var ultraRare = new AchievementRarityStats();

            foreach (var ach in achievements)
            {
                var tier = RarityHelper.GetRarityTier(ach.GlobalPercentUnlocked ?? 100);
                switch (tier)
                {
                    case RarityTier.UltraRare:
                        ultraRare.Total++;
                        if (ach.Unlocked) ultraRare.Unlocked++;
                        else ultraRare.Locked++;
                        break;
                    case RarityTier.Rare:
                        rare.Total++;
                        if (ach.Unlocked) rare.Unlocked++;
                        else rare.Locked++;
                        break;
                    case RarityTier.Uncommon:
                        uncommon.Total++;
                        if (ach.Unlocked) uncommon.Unlocked++;
                        else uncommon.Locked++;
                        break;
                    default:
                        common.Total++;
                        if (ach.Unlocked) common.Unlocked++;
                        else common.Locked++;
                        break;
                }
            }

            Common = common;
            Uncommon = uncommon;
            Rare = rare;
            UltraRare = ultraRare;
        }

        private void ClearData()
        {
            HasAchievements = false;
            AchievementCount = 0;
            UnlockedCount = 0;
            LockedCount = 0;
            ProgressPercentage = 0;
            IsCompleted = false;
            AllAchievements = new List<AchievementDetail>();
            AllAchievementDisplayItems = new List<AchievementDisplayItem>();
            Common = new AchievementRarityStats();
            Uncommon = new AchievementRarityStats();
            Rare = new AchievementRarityStats();
            UltraRare = new AchievementRarityStats();

            OnPropertyChanged(nameof(AchievementCount));
            OnPropertyChanged(nameof(UnlockedCount));
            OnPropertyChanged(nameof(LockedCount));
            OnPropertyChanged(nameof(ProgressPercentage));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(HasAchievements));
            OnPropertyChanged(nameof(Common));
            OnPropertyChanged(nameof(Uncommon));
            OnPropertyChanged(nameof(Rare));
            OnPropertyChanged(nameof(UltraRare));
            OnPropertyChanged(nameof(AllAchievements));
            OnPropertyChanged(nameof(AllAchievementDisplayItems));

            OnDataLoaded();
        }

        /// <summary>
        /// Called after data is loaded. Override to perform additional refresh.
        /// </summary>
        protected virtual void OnDataLoaded()
        {
        }

        /// <summary>
        /// Called when theme data changes. Triggers a data reload.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            LoadData();
        }

        /// <summary>
        /// Called when the game context changes.
        /// </summary>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            if (_isLoaded)
            {
                LoadData();
            }
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
