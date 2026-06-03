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
    }
}
