using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    /// <summary>The profile card's medal rows: order and on/off, and a saved Medals choice carried in.</summary>
    [TestClass]
    public class ProfileMedalRowsTests
    {
        private static ShowcaseWidgetInstanceSettings Profile(string medals = null)
        {
            var settings = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Profile };
            if (medals != null)
            {
                settings.Options["ProfileMedals"] = medals;
            }

            return settings;
        }

        private static string Describe(IEnumerable<ProgressDetailEntry> rows) =>
            string.Join(",", rows.Select(row => (row.Enabled ? string.Empty : "-") + row.Style));

        [TestMethod]
        public void Unset_ShowsRarityTiersOnly()
        {
            Assert.AreEqual("Rarity,-Trophy,-Points", Describe(ShowcaseWidgetOptions.GetProfileMedalRows(Profile())));
        }

        [TestMethod]
        public void SavedMedalsChoice_CarriesIntoTheRows()
        {
            Assert.AreEqual("Rarity,Trophy,-Points", Describe(ShowcaseWidgetOptions.GetProfileMedalRows(Profile("Both"))));
            Assert.AreEqual("Points,-Rarity,-Trophy", Describe(ShowcaseWidgetOptions.GetProfileMedalRows(Profile("Points"))));
            Assert.AreEqual("-Rarity,-Trophy,-Points", Describe(ShowcaseWidgetOptions.GetProfileMedalRows(Profile("None"))));
        }

        [TestMethod]
        public void SetRows_RoundTripsAndDropsTheOldChoice()
        {
            var settings = Profile("Trophy");
            ShowcaseWidgetOptions.SetProfileMedalRows(settings, new[]
            {
                new ProgressDetailEntry { Style = ProgressDetailStyle.Points, Enabled = true },
                new ProgressDetailEntry { Style = ProgressDetailStyle.Rarity, Enabled = false },
                new ProgressDetailEntry { Style = ProgressDetailStyle.Trophy, Enabled = true }
            });

            Assert.IsFalse(settings.Options.ContainsKey("ProfileMedals"));
            Assert.AreEqual("Points,-Rarity,Trophy", Describe(ShowcaseWidgetOptions.GetProfileMedalRows(settings)));
        }
    }
}
