using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    [DoNotParallelize]
    public class FormattingCultureTests
    {
        private CultureInfo _osCulture;

        [TestInitialize]
        public void PinOsCulture()
        {
            _osCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        }

        [TestCleanup]
        public void RestoreFormattingCulture()
        {
            CultureInfo.CurrentCulture = _osCulture;
            FormattingCulture.Initialize(() => "english");
        }

        [TestMethod]
        public void Current_UsesOsRegionalVariantOfTheSameLanguage()
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");

            FormattingCulture.Initialize(() => "english");
            Assert.AreEqual("en-GB", FormattingCulture.Current.Name);

            FormattingCulture.Initialize(() => "german");
            Assert.AreEqual("de-DE", FormattingCulture.Current.Name);
        }

        [TestMethod]
        public void Current_MapsKnownLanguagesToCultures()
        {
            FormattingCulture.Initialize(() => "german");
            Assert.AreEqual("de-DE", FormattingCulture.Current.Name);

            FormattingCulture.Initialize(() => "english");
            Assert.AreEqual("en-US", FormattingCulture.Current.Name);

            FormattingCulture.Initialize(() => "brazilian");
            Assert.AreEqual("pt-BR", FormattingCulture.Current.Name);

            FormattingCulture.Initialize(() => "schinese");
            Assert.AreEqual("zh-CN", FormattingCulture.Current.Name);
        }

        [TestMethod]
        public void Current_IsCaseInsensitiveAndTrimmed()
        {
            FormattingCulture.Initialize(() => " German ");
            Assert.AreEqual("de-DE", FormattingCulture.Current.Name);
        }

        [TestMethod]
        public void Current_FallsBackToOsCultureForUnknownOrEmptyValues()
        {
            FormattingCulture.Initialize(() => "klingon");
            Assert.AreEqual(CultureInfo.CurrentCulture, FormattingCulture.Current);

            FormattingCulture.Initialize(() => null);
            Assert.AreEqual(CultureInfo.CurrentCulture, FormattingCulture.Current);

            FormattingCulture.Initialize(() => string.Empty);
            Assert.AreEqual(CultureInfo.CurrentCulture, FormattingCulture.Current);
        }

        [TestMethod]
        public void XamlLanguage_MatchesResolvedCulture()
        {
            FormattingCulture.Initialize(() => "german");
            Assert.AreEqual("de-de", FormattingCulture.XamlLanguage.IetfLanguageTag);

            FormattingCulture.Initialize(() => "english");
            Assert.AreEqual("en-us", FormattingCulture.XamlLanguage.IetfLanguageTag);
        }
    }
}
