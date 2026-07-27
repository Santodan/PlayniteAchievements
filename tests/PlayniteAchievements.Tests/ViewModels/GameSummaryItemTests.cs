using System;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class GameSummaryItemTests
    {
        [TestMethod]
        public void ScoreFractions_FormatWithoutSpaces()
        {
            WithUsCulture(() =>
            {
                var item = new GameSummaryItem
                {
                    CollectionScore = 1,
                    CollectionScoreTotal = 10,
                    PrestigeScore = 1234,
                    PrestigeScoreTotal = 5678
                };

                Assert.AreEqual("1/10", item.CollectionScoreFractionText);
                Assert.AreEqual("1,234/5,678", item.PrestigeScoreFractionText);
            });
        }

        [TestMethod]
        public void ScoreFractions_ClampNegativeValuesToZero()
        {
            WithUsCulture(() =>
            {
                var item = new GameSummaryItem
                {
                    CollectionScore = -1,
                    CollectionScoreTotal = -10,
                    PrestigeScore = -1234,
                    PrestigeScoreTotal = 5678
                };

                Assert.AreEqual("0/0", item.CollectionScoreFractionText);
                Assert.AreEqual("0/5,678", item.PrestigeScoreFractionText);
            });
        }

        [TestMethod]
        public void OwnedText_IsNotExposed()
        {
            Assert.IsNull(typeof(GameSummaryItem).GetProperty("OwnedText"));
        }

        private static void WithUsCulture(Action action)
        {
            var previousCulture = Thread.CurrentThread.CurrentCulture;
            var previousUiCulture = Thread.CurrentThread.CurrentUICulture;

            try
            {
                var culture = CultureInfo.GetCultureInfo("en-US");
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                action();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previousCulture;
                Thread.CurrentThread.CurrentUICulture = previousUiCulture;
            }
        }
    }
}
