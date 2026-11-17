using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class InlineDetailTests
    {
        private static readonly ProgressDetailStyle[] TrophyRarityPoints =
        {
            ProgressDetailStyle.Trophy,
            ProgressDetailStyle.Rarity,
            ProgressDetailStyle.Points
        };

        private static ProgressDetailStyle? Pick(IEnumerable<ProgressDetailStyle> styles, params ProgressDetailStyle[] withData) =>
            ProgressDetailOrder.PickInline(styles, withData.Contains);

        [TestMethod]
        public void PickInline_TakesFirstStyleWithData()
        {
            // A PSN row has trophies and rarity; a Steam row only rarity; an Xbox row rarity and points.
            Assert.AreEqual(ProgressDetailStyle.Trophy, Pick(TrophyRarityPoints, ProgressDetailStyle.Trophy, ProgressDetailStyle.Rarity));
            Assert.AreEqual(ProgressDetailStyle.Rarity, Pick(TrophyRarityPoints, ProgressDetailStyle.Rarity));
            Assert.AreEqual(ProgressDetailStyle.Rarity, Pick(TrophyRarityPoints, ProgressDetailStyle.Rarity, ProgressDetailStyle.Points));
        }

        [TestMethod]
        public void PickInline_OnlyConsidersListedStyles()
        {
            var rarityOnly = new[] { ProgressDetailStyle.Rarity };

            Assert.IsNull(Pick(rarityOnly, ProgressDetailStyle.Trophy, ProgressDetailStyle.Points));
            Assert.IsNull(Pick(new ProgressDetailStyle[0], ProgressDetailStyle.Rarity));
            Assert.IsNull(ProgressDetailOrder.PickInline(null, style => true));
        }

        [TestMethod]
        public void PickInline_FollowsEnabledStylesOfThePriorityList()
        {
            var entries = new List<ProgressDetailEntry>
            {
                new ProgressDetailEntry { Style = ProgressDetailStyle.Points, Enabled = false },
                new ProgressDetailEntry { Style = ProgressDetailStyle.Trophy, Enabled = true },
                new ProgressDetailEntry { Style = ProgressDetailStyle.Rarity, Enabled = true }
            };

            var styles = ProgressDetailOrder.EnabledStyles(entries);

            Assert.AreEqual(ProgressDetailStyle.Rarity, Pick(styles, ProgressDetailStyle.Points, ProgressDetailStyle.Rarity));
        }

        [TestMethod]
        public void AchievementGridOptions_InlineDetailsDefaultOff()
        {
            var options = new AchievementGridOptions();

            Assert.IsFalse(options.InlineDetailsBeforeName);
            Assert.IsFalse(options.InlineDetailsBeforeDescription);
        }

        [TestMethod]
        public void AchievementGridOptions_InlineDetailsSurviveCloneAndJson()
        {
            var options = new AchievementGridOptions
            {
                InlineDetailsBeforeName = true,
                InlineDetailsBeforeDescription = true
            };

            var clone = options.Clone();
            var roundTrip = JsonConvert.DeserializeObject<AchievementGridOptions>(JsonConvert.SerializeObject(options));

            Assert.IsTrue(clone.InlineDetailsBeforeName);
            Assert.IsTrue(clone.InlineDetailsBeforeDescription);
            Assert.IsTrue(roundTrip.InlineDetailsBeforeName);
            Assert.IsTrue(roundTrip.InlineDetailsBeforeDescription);
        }
    }
}
