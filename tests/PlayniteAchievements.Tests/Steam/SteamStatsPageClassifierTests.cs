using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;
using System;
using System.IO;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamStatsPageClassifierTests
    {
        private const string StatsUrl = "https://steamcommunity.com/id/jdd056/stats/1233070/?tab=achievements";

        [TestMethod]
        public void LoggedOutFatalStatsPage_IsClassifiedAsUnauthenticated()
        {
            var html = LoadFixture("logged_out_fatal_stats.html");

            Assert.IsTrue(SteamStatsPageClassifier.LooksUnauthenticatedStatsPayload(html, StatsUrl));
            Assert.IsFalse(SteamStatsPageClassifier.LooksPrivateOrRestrictedStatsPayload(html, StatsUrl));
            Assert.IsTrue(SteamStatsPageClassifier.LooksLoggedOutHeader(html));
            Assert.IsTrue(SteamStatsPageClassifier.LooksLoggedOutStatsPayloadWithoutRows(html, StatsUrl));
        }

        [TestMethod]
        public void LoggedInPrivateFatalStatsPage_IsClassifiedAsPrivate()
        {
            var html = LoadFixture("logged_in_private_fatal_stats.html");

            Assert.IsFalse(SteamStatsPageClassifier.LooksUnauthenticatedStatsPayload(html, StatsUrl));
            Assert.IsTrue(SteamStatsPageClassifier.LooksPrivateOrRestrictedStatsPayload(html, StatsUrl));
            Assert.IsFalse(SteamStatsPageClassifier.LooksLoggedOutHeader(html));
            Assert.IsFalse(SteamStatsPageClassifier.LooksLoggedOutStatsPayloadWithoutRows(html, StatsUrl));
        }

        [TestMethod]
        public void LoggedOutPublicStatsPage_WithAchievementRows_IsUsable()
        {
            const string html =
                "<html><body>" +
                "<a class='global_action_link' href='https://steamcommunity.com/login/home/'>Sign in</a>" +
                "<div id='personalAchieve'><div class='achieveRow'>Achievement</div></div>" +
                "<script>g_steamID = false;</script>" +
                "</body></html>";

            Assert.IsFalse(SteamStatsPageClassifier.LooksUnauthenticatedStatsPayload(html, StatsUrl));
            Assert.IsTrue(SteamStatsPageClassifier.LooksLoggedOutHeader(html));
            Assert.IsFalse(SteamStatsPageClassifier.LooksLoggedOutStatsPayloadWithoutRows(html, StatsUrl));
        }

        private static string LoadFixture(string fileName)
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Steam", fileName);
            return File.ReadAllText(fixturePath);
        }
    }
}
