using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.PSN;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class PsnCategoryResolutionTests
    {
        private static IReadOnlyDictionary<string, string> Groups()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = "Base Game",
                ["001"] = "Frozen Wilds",
                ["002"] = "   ",
            };
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("default")]
        public void ResolveCategory_BaseGroup_ReturnsGroupTitle(string groupId)
        {
            // The base/default group (id normalizes to "default") takes its own title, like DLC groups.
            Assert.AreEqual("Base Game", PsnTrophyCategoryHelper.ResolveCategory(groupId, Groups()));
        }

        [TestMethod]
        public void ResolveCategory_DlcGroupWithName_ReturnsTrimmedName()
        {
            Assert.AreEqual("Frozen Wilds", PsnTrophyCategoryHelper.ResolveCategory("001", Groups()));
        }

        [TestMethod]
        public void ResolveCategory_DlcGroupWithBlankName_ReturnsNull()
        {
            Assert.IsNull(PsnTrophyCategoryHelper.ResolveCategory("002", Groups()));
        }

        [TestMethod]
        public void ResolveCategory_DlcGroupMissingFromMap_ReturnsNull()
        {
            Assert.IsNull(PsnTrophyCategoryHelper.ResolveCategory("999", Groups()));
        }

        [TestMethod]
        public void ResolveCategory_NullMap_ReturnsNull()
        {
            Assert.IsNull(PsnTrophyCategoryHelper.ResolveCategory("001", null));
        }

        [DataTestMethod]
        [DataRow(null, "Base")]
        [DataRow("", "Base")]
        [DataRow("default", "Base")]
        [DataRow("base", "Base")]
        [DataRow("000", "Base")]
        [DataRow("001", "DLC")]
        [DataRow("anything-else", "DLC")]
        public void MapTrophyGroupToCategoryType_ClassifiesBaseVsDlc(string groupId, string expected)
        {
            Assert.AreEqual(expected, PsnTrophyCategoryHelper.MapTrophyGroupToCategoryType(groupId));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("default")]
        public void ResolveCollectionCategory_BaseGroup_ReturnsSetTitle(string groupId)
        {
            // Each included game of a collection renders as its own category, named by its set,
            // rather than by the shared base-group title.
            Assert.AreEqual(
                "Spyro the Dragon",
                PsnTrophyCategoryHelper.ResolveCollectionCategory(groupId, Groups(), "  Spyro the Dragon  "));
        }

        [TestMethod]
        public void ResolveCollectionCategory_DlcGroupWithName_ReturnsTitleDashGroup()
        {
            Assert.AreEqual(
                "Horizon Zero Dawn - Frozen Wilds",
                PsnTrophyCategoryHelper.ResolveCollectionCategory("001", Groups(), "Horizon Zero Dawn"));
        }

        [DataTestMethod]
        [DataRow("002")]
        [DataRow("999")]
        public void ResolveCollectionCategory_DlcGroupWithoutName_ReturnsSetTitle(string groupId)
        {
            Assert.AreEqual(
                "Horizon Zero Dawn",
                PsnTrophyCategoryHelper.ResolveCollectionCategory(groupId, Groups(), "Horizon Zero Dawn"));
        }

        [TestMethod]
        public void ResolveCollectionCategory_NoSetTitle_FallsBackToGroupNameForDlc()
        {
            // Without a set title a named DLC group still labels itself; the base group has no
            // label left to use, so the hydrator renders its localized default.
            Assert.AreEqual(
                "Frozen Wilds",
                PsnTrophyCategoryHelper.ResolveCollectionCategory("001", Groups(), "   "));
            Assert.IsNull(PsnTrophyCategoryHelper.ResolveCollectionCategory("default", Groups(), null));
        }

        [TestMethod]
        public void ResolveCollectionCategory_NullMap_ReturnsSetTitle()
        {
            Assert.AreEqual(
                "Spyro the Dragon",
                PsnTrophyCategoryHelper.ResolveCollectionCategory("001", null, "Spyro the Dragon"));
        }
    }
}
