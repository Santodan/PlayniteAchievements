using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    [DoNotParallelize]
    public class PercentFormatterTests
    {
        [TestInitialize]
        public void PinFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        [TestCleanup]
        public void RestoreFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        private static void WithLanguage(string language, Action assertions)
        {
            FormattingCulture.Initialize(() => language);
            try
            {
                assertions();
            }
            finally
            {
                FormattingCulture.Initialize(() => "english");
            }
        }

        [TestMethod]
        public void Format_EnglishMatchesLegacyOutput()
        {
            Assert.AreEqual("12.3%", PercentFormatter.Format(12.34, 1));
            Assert.AreEqual("12%", PercentFormatter.FormatWhole(12.34));
            Assert.AreEqual("100%", PercentFormatter.FormatWhole(100));
            Assert.AreEqual("0%", PercentFormatter.FormatWhole(0.4));
        }

        [TestMethod]
        public void Format_NanAndInfinityReturnEmpty()
        {
            Assert.AreEqual(string.Empty, PercentFormatter.Format(double.NaN, 1));
            Assert.AreEqual(string.Empty, PercentFormatter.FormatWhole(double.NaN));
            Assert.AreEqual(string.Empty, PercentFormatter.FormatWhole(double.PositiveInfinity));
            Assert.AreEqual(string.Empty, PercentFormatter.FormatWhole(double.NegativeInfinity));
        }

        [TestMethod]
        public void FormatWhole_EnglishRoundsMidpointAwayFromZero()
        {
            Assert.AreEqual("13%", PercentFormatter.FormatWhole(12.5));
        }

        [TestMethod]
        public void FormatLessThanWhole_EnglishHasNoSpaces()
        {
            Assert.AreEqual("<1%", PercentFormatter.FormatLessThanWhole(1));
        }

        [TestMethod]
        public void Format_GermanUsesCommaAndSpacedPercentSign()
        {
            WithLanguage("german", () =>
            {
                Assert.AreEqual("12,3 %", PercentFormatter.Format(12.34, 1));
                Assert.AreEqual("100 %", PercentFormatter.FormatWhole(100));
            });
        }

        [TestMethod]
        public void FormatLessThanWhole_GermanSpacesTheLessThanSign()
        {
            WithLanguage("german", () =>
            {
                Assert.AreEqual("< 1 %", PercentFormatter.FormatLessThanWhole(1));
            });
        }
    }
}
