using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ViewAchievementsSummarySettingsTests
    {
        [TestMethod]
        public void NewSurface_HasExpectedDefaults()
        {
            var settings = new PersistedSettings();

            Assert.IsFalse(settings.ViewAchievementsGameSummariesUseCoverImages);
            Assert.IsTrue(settings.ViewAchievementsGameSummariesShowMetadataPlatform);
            Assert.IsTrue(settings.ViewAchievementsGameSummariesShowMetadataPlaytime);
            Assert.IsTrue(settings.ViewAchievementsGameSummariesShowMetadataRegion);
            Assert.IsTrue(settings.ViewAchievementsGameSummariesShowCompletionGlow);
            Assert.IsTrue(settings.ShowViewAchievementsGameSummariesGridColumnHeaders);
            Assert.IsNull(settings.ViewAchievementsGameSummariesGridRowHeight);
        }

        [TestMethod]
        public void ColumnVisibility_IsIndependentFromOverviewSurface()
        {
            var settings = new PersistedSettings
            {
                ViewAchievementsGameSummariesColumnVisibility =
                    new Dictionary<string, bool> { ["GameSummaryProgression"] = false },
                OverviewGameSummariesColumnVisibility =
                    new Dictionary<string, bool> { ["GameSummaryProgression"] = true }
            };

            Assert.IsFalse(settings.ViewAchievementsGameSummariesColumnVisibility["GameSummaryProgression"]);
            Assert.IsTrue(settings.OverviewGameSummariesColumnVisibility["GameSummaryProgression"]);
        }

        [TestMethod]
        public void Clone_CopiesNewSurfaceAndIsDeep()
        {
            var settings = new PersistedSettings
            {
                ViewAchievementsGameSummariesUseCoverImages = true,
                ViewAchievementsGameSummariesShowMetadataPlatform = false,
                ViewAchievementsGameSummariesShowMetadataPlaytime = false,
                ViewAchievementsGameSummariesShowMetadataRegion = false,
                ViewAchievementsGameSummariesGridRowHeight = 48,
                ViewAchievementsGameSummariesColumnWidths =
                    new Dictionary<string, double> { ["Cover"] = 120 }
            };

            var clone = settings.Clone();

            Assert.IsTrue(clone.ViewAchievementsGameSummariesUseCoverImages);
            Assert.IsFalse(clone.ViewAchievementsGameSummariesShowMetadataPlatform);
            Assert.IsFalse(clone.ViewAchievementsGameSummariesShowMetadataPlaytime);
            Assert.IsFalse(clone.ViewAchievementsGameSummariesShowMetadataRegion);
            Assert.AreEqual(48, clone.ViewAchievementsGameSummariesGridRowHeight);
            Assert.AreEqual(120, clone.ViewAchievementsGameSummariesColumnWidths["Cover"]);

            // Deep copy: mutating the clone does not affect the original dictionary.
            clone.ViewAchievementsGameSummariesColumnWidths["Cover"] = 200;
            Assert.AreEqual(120, settings.ViewAchievementsGameSummariesColumnWidths["Cover"]);
        }
    }
}
