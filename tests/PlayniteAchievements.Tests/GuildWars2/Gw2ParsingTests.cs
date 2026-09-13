using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.GuildWars2;

namespace PlayniteAchievements.GuildWars2.Tests
{
    [TestClass]
    public class Gw2ParsingTests
    {
        [DataTestMethod]
        [DataRow("Guild Wars 2")]
        [DataRow("guild wars 2")]
        [DataRow("GUILD WARS 2")]
        [DataRow("Guild Wars 2®")]
        [DataRow("Guild Wars 2 (Steam)")]
        [DataRow("Guild Wars 2: Secrets of the Obscure")]
        [DataRow("GuildWars2")]
        [DataRow("GW2")]
        public void IsGuildWars2Title_MatchesKnownTitleForms(string title)
        {
            // The Steam entry really is "Guild Wars 2" with a registered-trademark sign, which an
            // equality test against one spelling misses.
            Assert.IsTrue(Gw2Parsing.IsGuildWars2Title(title), title);
        }

        [DataTestMethod]
        [DataRow("Guild Wars")]
        [DataRow("Guild Wars: Nightfall")]
        [DataRow("Guilty Gear")]
        [DataRow("Final Fantasy XIV")]
        [DataRow("")]
        [DataRow(null)]
        public void IsGuildWars2Title_RejectsOtherTitles(string title)
        {
            // The original Guild Wars normalizes to "guildwars" and has its own, separate API.
            Assert.IsFalse(Gw2Parsing.IsGuildWars2Title(title), title ?? "null");
        }

        [DataTestMethod]
        [DataRow("english", "en")]
        [DataRow("German", "de")]
        [DataRow("french", "fr")]
        [DataRow("spanish", "es")]
        [DataRow("latam", "es")]
        [DataRow("schinese", "zh")]
        [DataRow("tchinese", "zh")]
        public void MapGlobalLanguage_MapsPluginLanguageNames(string input, string expected)
        {
            Assert.AreEqual(expected, Gw2Parsing.MapGlobalLanguage(input));
        }

        [DataTestMethod]
        [DataRow("de", "de")]
        [DataRow("de-DE", "de")]
        [DataRow("fr_FR", "fr")]
        [DataRow("ZH-CN", "zh")]
        public void MapGlobalLanguage_AcceptsLocaleCodes(string input, string expected)
        {
            Assert.AreEqual(expected, Gw2Parsing.MapGlobalLanguage(input));
        }

        [DataTestMethod]
        [DataRow("japanese")]
        [DataRow("ru")]
        [DataRow("pt-BR")]
        [DataRow("")]
        [DataRow(null)]
        public void MapGlobalLanguage_FallsBackToEnglishForUnsupportedLanguages(string input)
        {
            // The API serves only five languages; anything else would return untranslated text.
            Assert.AreEqual("en", Gw2Parsing.MapGlobalLanguage(input));
        }
    }
}
