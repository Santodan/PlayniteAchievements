using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class GridOptionsNotificationBridgeTests
    {
        [TestMethod]
        public void AchievementOptionEdit_RaisesFlatPropertyChangedOnPersistedSettings()
        {
            var settings = new PersistedSettings();
            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            var options = settings.GridOptions.GetAchievement(GridOptionKeys.Achievement.OverviewRecent);
            options.ShowRarityGlow = !options.ShowRarityGlow;
            options.ColorRarityColumnsByRarity = !options.ColorRarityColumnsByRarity;

            CollectionAssert.Contains(raised, nameof(PersistedSettings.OverviewRecentAchievementsShowRarityGlow));
            CollectionAssert.Contains(raised, nameof(PersistedSettings.OverviewRecentAchievementsColorRarityColumnsByRarity));
        }

        [TestMethod]
        public void GameSummariesOptionEdit_RaisesFlatPropertyChangedOnPersistedSettings()
        {
            var settings = new PersistedSettings();
            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            var options = settings.GridOptions.GetGameSummaries(GridOptionKeys.GameSummaries.Overview);
            options.SortDescending = !options.SortDescending;
            options.ShowNameAboveProgress = !options.ShowNameAboveProgress;

            CollectionAssert.Contains(raised, nameof(PersistedSettings.OverviewGameSummariesGridSortDescending));
            CollectionAssert.Contains(raised, nameof(PersistedSettings.OverviewGameSummariesShowNameAboveProgress));
        }

        [TestMethod]
        public void FriendSummariesOptionEdit_RaisesFlatPropertyChangedOnPersistedSettings()
        {
            var settings = new PersistedSettings();
            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            var options = settings.GridOptions.GetFriendSummaries(GridOptionKeys.FriendSummaries.FriendsOverview);
            options.LastUnlockDateMode = options.LastUnlockDateMode == DateDisplayMode.Relative
                ? DateDisplayMode.DateOnly
                : DateDisplayMode.Relative;

            CollectionAssert.Contains(raised, nameof(PersistedSettings.FriendsOverviewFriendSummariesLastUnlockDateMode));
        }

        [TestMethod]
        public void CategorySummariesOptionEdit_RaisesFlatPropertyChangedOnPersistedSettings()
        {
            var settings = new PersistedSettings();
            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            var options = settings.GridOptions.CategorySummaries[GridOptionKeys.CategorySummaries.FriendsOverview];
            options.UseCoverImages = !options.UseCoverImages;
            options.ShowNameAboveProgress = !options.ShowNameAboveProgress;

            CollectionAssert.Contains(raised, nameof(PersistedSettings.FriendsOverviewCategorySummariesUseCoverImages));
            CollectionAssert.Contains(raised, nameof(PersistedSettings.FriendsOverviewCategorySummariesShowNameAboveProgress));
        }

        [TestMethod]
        public void AchievementHideCategorySummaryRowEdit_RaisesFlatPropertyChangedOnPersistedSettings()
        {
            var settings = new PersistedSettings();
            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            var options = settings.GridOptions.Achievement[GridOptionKeys.Achievement.OverviewSelectedGame];
            options.HideCategorySummaryRow = !options.HideCategorySummaryRow;

            CollectionAssert.Contains(raised, nameof(PersistedSettings.OverviewSelectedGameAchievementsHideCategorySummaryRow));
        }

        [TestMethod]
        public void GridOptionsReplacement_ReattachesBridgeToNewCatalog()
        {
            var settings = new PersistedSettings();
            settings.GridOptions = new GridOptionsCatalog();

            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            var options = settings.GridOptions.GetAchievement(GridOptionKeys.Achievement.OverviewSelectedGame);
            options.StartInCategoryMode = !options.StartInCategoryMode;

            CollectionAssert.Contains(raised, nameof(PersistedSettings.OverviewSelectedGameAchievementsStartInCategoryMode));
        }

        [TestMethod]
        public void OptionEditOnReplacedCatalog_DoesNotRaiseFromStaleCatalog()
        {
            var settings = new PersistedSettings();
            var staleOptions = settings.GridOptions.GetAchievement(GridOptionKeys.Achievement.OverviewRecent);
            settings.GridOptions = new GridOptionsCatalog();

            var raised = new List<string>();
            settings.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

            staleOptions.ShowRarityGlow = !staleOptions.ShowRarityGlow;

            CollectionAssert.DoesNotContain(raised, nameof(PersistedSettings.OverviewRecentAchievementsShowRarityGlow));
        }
    }
}
