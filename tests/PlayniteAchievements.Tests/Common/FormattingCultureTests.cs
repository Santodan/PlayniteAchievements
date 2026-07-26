using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    [DoNotParallelize]
    public class FormattingCultureTests
    {
        [TestCleanup]
        public void RestoreFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
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
