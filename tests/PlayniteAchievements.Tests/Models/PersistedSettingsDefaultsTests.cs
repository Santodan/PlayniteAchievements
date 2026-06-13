using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class PersistedSettingsDefaultsTests
    {
        [TestMethod]
        public void Constructor_DefaultsAchievementDataGridMaxHeight()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(
                PersistedSettings.DefaultAchievementDataGridMaxHeight,
                settings.AchievementDataGridMaxHeight);
        }

        [TestMethod]
        public void Constructor_DefaultsSidebarOverviewColumnRatio()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(
                PersistedSettings.DefaultSidebarOverviewLeftColumnRatio,
                settings.SidebarOverviewLeftColumnRatio);
        }

        [TestMethod]
        public void Constructor_DefaultsSidebarScoreCardsVisible()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.ShowSidebarCollectionScoreCard);
            Assert.IsTrue(settings.ShowSidebarPrestigeScoreCard);
        }

        [TestMethod]
        public void SidebarOverviewColumnRatio_ClampsInvalidValues()
        {
            var settings = new PersistedSettings();

            settings.SidebarOverviewLeftColumnRatio = -1d;
            Assert.AreEqual(
                PersistedSettings.MinSidebarOverviewLeftColumnRatio,
                settings.SidebarOverviewLeftColumnRatio);

            settings.SidebarOverviewLeftColumnRatio = 2d;
            Assert.AreEqual(
                PersistedSettings.MaxSidebarOverviewLeftColumnRatio,
                settings.SidebarOverviewLeftColumnRatio);

            settings.SidebarOverviewLeftColumnRatio = double.NaN;
            Assert.AreEqual(
                PersistedSettings.DefaultSidebarOverviewLeftColumnRatio,
                settings.SidebarOverviewLeftColumnRatio);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveSidebarOverviewColumnRatio()
        {
            var source = new PersistedSettings
            {
                SidebarOverviewLeftColumnRatio = 0.64d
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(0.64d, clone.SidebarOverviewLeftColumnRatio);
            Assert.AreEqual(0.64d, target.SidebarOverviewLeftColumnRatio);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveSidebarScoreCardVisibility()
        {
            var source = new PersistedSettings
            {
                ShowSidebarCollectionScoreCard = false,
                ShowSidebarPrestigeScoreCard = false
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsFalse(clone.ShowSidebarCollectionScoreCard);
            Assert.IsFalse(clone.ShowSidebarPrestigeScoreCard);
            Assert.IsFalse(target.ShowSidebarCollectionScoreCard);
            Assert.IsFalse(target.ShowSidebarPrestigeScoreCard);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveLastAllGamesScoreSnapshot()
        {
            var source = new PersistedSettings
            {
                LastAllGamesCollectorScore = 1234,
                LastAllGamesCollectorLevel = 7,
                LastAllGamesCollectorLevelProgress = 42.5,
                LastAllGamesCollectorRank = "Silver1",
                LastAllGamesPrestigeScore = 5678,
                LastAllGamesPrestigeLevel = 11,
                LastAllGamesPrestigeLevelProgress = 66.25,
                LastAllGamesPrestigeRank = "Gold2"
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(1234, clone.LastAllGamesCollectorScore);
            Assert.AreEqual(7, clone.LastAllGamesCollectorLevel);
            Assert.AreEqual(42.5, clone.LastAllGamesCollectorLevelProgress);
            Assert.AreEqual("Silver1", clone.LastAllGamesCollectorRank);
            Assert.AreEqual(5678, clone.LastAllGamesPrestigeScore);
            Assert.AreEqual(11, clone.LastAllGamesPrestigeLevel);
            Assert.AreEqual(66.25, clone.LastAllGamesPrestigeLevelProgress);
            Assert.AreEqual("Gold2", clone.LastAllGamesPrestigeRank);

            Assert.AreEqual(1234, target.LastAllGamesCollectorScore);
            Assert.AreEqual(7, target.LastAllGamesCollectorLevel);
            Assert.AreEqual(42.5, target.LastAllGamesCollectorLevelProgress);
            Assert.AreEqual("Silver1", target.LastAllGamesCollectorRank);
            Assert.AreEqual(5678, target.LastAllGamesPrestigeScore);
            Assert.AreEqual(11, target.LastAllGamesPrestigeLevel);
            Assert.AreEqual(66.25, target.LastAllGamesPrestigeLevelProgress);
            Assert.AreEqual("Gold2", target.LastAllGamesPrestigeRank);
        }
    }
}
