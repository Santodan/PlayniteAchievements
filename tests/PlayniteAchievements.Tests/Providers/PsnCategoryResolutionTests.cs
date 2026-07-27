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
    }
}
