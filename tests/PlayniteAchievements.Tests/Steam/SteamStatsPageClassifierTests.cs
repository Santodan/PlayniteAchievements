using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        [TestMethod]
        public void AllHiddenLockedStatsPage_HasOnlyHiddenAchievementRows()
        {
            var html = LoadFixture("all_hidden_locked_stats.html");

            Assert.IsTrue(SteamStatsPageClassifier.HasOnlyHiddenAchievementRows(html));
        }

        [TestMethod]
        public void AllHiddenLockedStatsPage_HiddenRemainingCount_IsParsed()
        {
            var html = LoadFixture("all_hidden_locked_stats.html");

            Assert.AreEqual(10, SteamStatsPageClassifier.TryGetHiddenRemainingCount(html));
        }

        [TestMethod]
        public void AllHiddenLockedStatsPage_IsNotClassifiedAsUnauthenticatedOrPrivate()
        {
            var html = LoadFixture("all_hidden_locked_stats.html");

            Assert.IsFalse(SteamStatsPageClassifier.LooksUnauthenticatedStatsPayload(html, StatsUrl));
            Assert.IsFalse(SteamStatsPageClassifier.LooksPrivateOrRestrictedStatsPayload(html, StatsUrl));
            Assert.IsFalse(SteamStatsPageClassifier.LooksLoggedOutHeader(html));
        }

        [TestMethod]
        public void FatalStatsPages_AreNotAllHiddenRows()
        {
            foreach (var fixture in new[] { "logged_out_fatal_stats.html", "logged_in_private_fatal_stats.html" })
            {
                var html = LoadFixture(fixture);

                Assert.IsFalse(SteamStatsPageClassifier.HasOnlyHiddenAchievementRows(html), fixture);
                Assert.IsNull(SteamStatsPageClassifier.TryGetHiddenRemainingCount(html), fixture);
            }
        }

        [TestMethod]
        public void ConfirmsAllHiddenZeroUnlocks_AllHiddenSchema_CountAbsent_Confirms()
        {
            Assert.IsTrue(SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(AllHiddenSchema(10), null));
        }

        [TestMethod]
        public void ConfirmsAllHiddenZeroUnlocks_AllHiddenSchema_CountMatches_Confirms()
        {
            Assert.IsTrue(SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(AllHiddenSchema(10), 10));
        }

        [TestMethod]
        public void ConfirmsAllHiddenZeroUnlocks_CountMismatch_DoesNotConfirm()
        {
            Assert.IsFalse(SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(AllHiddenSchema(10), 9));
        }

        [TestMethod]
        public void ConfirmsAllHiddenZeroUnlocks_VisibleAchievementInSchema_DoesNotConfirm()
        {
            var schema = AllHiddenSchema(10);
            schema.Achievements[3].Hidden = 0;

            Assert.IsFalse(SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(schema, null));
        }

        [TestMethod]
        public void ConfirmsAllHiddenZeroUnlocks_MissingOrEmptySchema_DoesNotConfirm()
        {
            Assert.IsFalse(SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(null, null));
            Assert.IsFalse(SteamStatsPageClassifier.ConfirmsAllHiddenZeroUnlocks(
                new SchemaAndPercentages { Achievements = new List<SchemaAchievement>() },
                null));
        }

        private static SchemaAndPercentages AllHiddenSchema(int count)
        {
            return new SchemaAndPercentages
            {
                Achievements = Enumerable.Range(0, count)
                    .Select(i => new SchemaAchievement { Name = $"ACH_{i}", Hidden = 1 })
                    .ToList()
            };
        }

        private static string LoadFixture(string fileName)
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Steam", fileName);
            return File.ReadAllText(fixturePath);
        }
    }
}
