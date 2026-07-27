using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamWebAuthParserTests
    {
        [TestMethod]
        public void Parse_ExtractsPlayniteSteamLibraryTokenShape()
        {
            const string source =
                "var g_steamID = \"76561198000000000\";" +
                "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("76561198000000000", session.SteamId64);
            Assert.AreEqual("store-token", session.WebApiToken);
        }

        [TestMethod]
        public void Parse_SupportsCookieSteamId_WhenPageOnlyHasToken()
        {
            const string source = "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source, "76561198000000000", hasSteamSessionCookies: true);

            Assert.IsTrue(session.IsComplete);
            Assert.IsTrue(session.HasSteamSessionCookies);
            Assert.AreEqual("76561198000000000", session.SteamId64);
        }

        [TestMethod]
        public void Parse_PrefersLoyaltyWebApiToken_WhenPresent()
        {
            const string source =
                "var g_steamID = \"76561198000000000\";" +
                "data-loyalty_webapi_token=\"oauth-token\"" +
                "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("76561198000000000", session.SteamId64);
            Assert.AreEqual("oauth-token", session.WebApiToken);
        }

        [TestMethod]
        public void Parse_PrefersPageSteamId_OverCookieSteamId()
        {
            const string source =
                "var g_steamID = \"76561198000000002\";" +
                "data-userinfo=\"{&quot;webapi_token&quot;:&quot;store-token&quot;}\"";

            var session = SteamWebAuthParser.Parse(source, "76561198000000001", hasSteamSessionCookies: true);

            Assert.IsTrue(session.IsComplete);
            Assert.AreEqual("76561198000000002", session.SteamId64);
        }

        [TestMethod]
        public void Parse_DoesNotCompleteSession_WhenTokenMissing()
        {
            const string source = "var g_steamID = \"76561198000000000\";";

            var session = SteamWebAuthParser.Parse(source, hasSteamSessionCookies: true);

            Assert.IsTrue(session.HasSteamId);
            Assert.IsFalse(session.HasWebApiToken);
            Assert.IsFalse(session.IsComplete);
        }
    }
}
