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

        /// <summary>
        /// Every tag form present in the live catalog, with the counts observed across its 6,879
        /// achievements: </c> 194, <c=@flavor> 89, <c=@reminder> 86, <br> 60, <c=@Flavor> 19 and
        /// <c=flavor> 1. The capitalisation and the missing @ are real, not hypothetical.
        /// </summary>
        [DataTestMethod]
        [DataRow("<c=@flavor>Tinted prose.</c>", "Tinted prose.")]
        [DataRow("<c=@Flavor>Capitalised tag.</c>", "Capitalised tag.")]
        [DataRow("<c=flavor>No at sign.</c>", "No at sign.")]
        [DataRow("<c=@reminder>A reminder.</c>", "A reminder.")]
        public void StripMarkup_RemovesEveryColorTagFormInLiveData(string input, string expected)
        {
            Assert.AreEqual(expected, Gw2Parsing.StripMarkup(input));
        }

        [TestMethod]
        public void StripMarkup_KeepsTheProseAroundAnInlineTag()
        {
            // The real shape: plain requirement text, then tinted guidance appended.
            var input = "Activate your profession mechanic skills times.\n\n" +
                        "<c=@reminder>Profession mechanic skills appear above your weapon skills.</c>";

            Assert.AreEqual(
                "Activate your profession mechanic skills times.\n\n" +
                "Profession mechanic skills appear above your weapon skills.",
                Gw2Parsing.StripMarkup(input));
        }

        [TestMethod]
        public void StripMarkup_TurnsLineBreakTagsIntoRealNewlines()
        {
            // Dropping these outright would run two paragraphs together.
            Assert.AreEqual("First.\nSecond.", Gw2Parsing.StripMarkup("First.<br>Second."));
            Assert.AreEqual("First.\nSecond.", Gw2Parsing.StripMarkup("First.<BR/>Second."));
        }

        [TestMethod]
        public void StripMarkup_CollapsesTheGapLeftByATemplatedCount()
        {
            // 1,575 requirements arrive with the target count templated out.
            Assert.AreEqual("Kill player in PvP.", Gw2Parsing.StripMarkup("Kill  player in PvP."));
        }

        [TestMethod]
        public void StripMarkup_TrimsTheTrailingSpaceFlavourTextEndsOn()
        {
            // Live flavour text habitually ends on a space before its closing tag.
            Assert.AreEqual(
                "The peoples of Elona are adept in ways of combat.",
                Gw2Parsing.StripMarkup("<c=@Flavor>The peoples of Elona are adept in ways of combat. </c>"));
        }

        [DataTestMethod]
        [DataRow("Plain text with no markup at all.")]
        [DataRow("")]
        [DataRow(null)]
        public void StripMarkup_LeavesUnmarkedTextAlone(string input)
        {
            Assert.AreEqual(input, Gw2Parsing.StripMarkup(input));
        }

        [TestMethod]
        public void StripMarkup_DoesNotEatLegitimateAngleBrackets()
        {
            // Only c and br tags are recognized; arithmetic and other text survive.
            Assert.AreEqual("Finish in < 5 minutes.", Gw2Parsing.StripMarkup("Finish in < 5 minutes."));
            Assert.AreEqual("Reach <Rank> status.", Gw2Parsing.StripMarkup("Reach <Rank> status."));
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
