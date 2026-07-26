using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class ExophaseFriendPlaytimeTests
    {
        [TestMethod]
        public void ApiOwnership_WiresAchievementCountsIntoOwnershipHints()
        {
            var provider = File.ReadAllText(
                FindRepoFile("source", "Providers", "Exophase", "ExophaseFriendsProvider.cs"));

            // The games-list API supplies earned/total counts on every row...
            StringAssert.Contains(provider, "AchievementsEarned = apiGame.EarnedAwards");
            StringAssert.Contains(provider, "AchievementsTotal = apiGame.TotalAwards");

            // ...and the earned/total values flow into the ownership unlock hint.
            StringAssert.Contains(provider, "AchievementUnlocksHint = game.AchievementsEarned");
            StringAssert.Contains(provider, "AchievementTotalHint = game.AchievementsTotal");
        }

        [TestMethod]
        public void GetFriendGameDefinition_ParsesHeaderBannerFromSameFetch()
        {
            var provider = File.ReadAllText(
                FindRepoFile("source", "Providers", "Exophase", "ExophaseFriendsProvider.cs"));

            // The definition fetch returns the page HTML too, and the game header banner is parsed from
            // that same HTML (no second request) and attached to the definition so provider-only friend
            // games get a full-size icon/cover.
            StringAssert.Contains(provider, "FetchAchievementsWithHtmlAsync");
            StringAssert.Contains(provider, "ExophaseFriendPageParser.ParseGameHeaderImageUrl(fetched.Html)");
            StringAssert.Contains(provider, "IconUrl = headerImageUrl");
        }

        [TestMethod]
        public void ProviderOnlyProbe_PersistsHeaderBannerInsteadOfProfileThumbnail()
        {
            var runtime = File.ReadAllText(
                FindRepoFile("source", "Services", "Refresh", "FriendRefreshCoordinator.cs"));

            // The provider-only probe persists the scraped header banner as the game's icon and cover.
            StringAssert.Contains(runtime, "!string.IsNullOrWhiteSpace(achievements.IconUrl)");
            StringAssert.Contains(runtime, "achievements.IconUrl,");

            // For banner-preferring providers the profile-thumbnail download is skipped so it cannot
            // overwrite the higher-quality banner via COALESCE.
            StringAssert.Contains(runtime, "!FriendRefreshWorkPolicy.PrefersDefinitionHeaderBannerImages(providerKey)");
        }

        [TestMethod]
        public void ExtractSteamStoreAppId_ReadsGameInfoSteamStoreLink()
        {
            var html =
                @"<dl class=""details""><dt>Links:</dt><dd><a rel=""nofollow"" target=""_blank"" href=""https://store.steampowered.com/app/3768760"">Steam Store</a></dd></dl>";

            Assert.AreEqual(3768760, ExophaseSteamAppIdParser.Extract(html));
        }

        [TestMethod]
        public void ParsePlaytimeMinutes_AcceptsCommaDecimalAndNormalizesToDot()
        {
            var parser = File.ReadAllText(
                FindRepoFile("source", "Providers", "Exophase", "ExophaseFriendPageParser.cs"));

            // The hours group accepts a comma or dot decimal, and the comma is normalized to a dot
            // before invariant parsing so French values like "12,5 h" parse correctly.
            StringAssert.Contains(parser, @"(?:(\d+(?:[.,]\d+)?)\s*h(?:ours?)?)?");
            StringAssert.Contains(parser, "match.Groups[1].Value.Replace(',', '.')");
        }

        [TestMethod]
        public void SteamOwnershipSupplement_SkipsKnownSteamRowsBeforePageFetch()
        {
            var provider = File.ReadAllText(
                FindRepoFile("source", "Providers", "Exophase", "ExophaseFriendsProvider.cs"));

            StringAssert.Contains(provider, "BuildKnownSteamOwnershipIndex(knownSteamOwnership)");
            StringAssert.Contains(provider, "IsKnownSteamOwnership(game, knownSteamGames)");
            StringAssert.Contains(provider, "HasPositiveAchievementUnlock(game)");
            StringAssert.Contains(provider, "ResolveSteamAppIdForSupplementAsync(game, cancel)");
        }

        [TestMethod]
        public void ExophaseFriendPlatformSelection_DoesNotOfferSteam()
        {
            var catalog = File.ReadAllText(
                FindRepoFile("source", "Providers", "Exophase", "ExophaseFriendPlatformCatalog.cs"));
            var settings = File.ReadAllText(
                FindRepoFile("source", "Providers", "Exophase", "ExophaseSettings.cs"));

            Assert.IsFalse(catalog.Contains("new Entry(\"steam\""));
            StringAssert.Contains(settings, "!string.Equals(platform, \"steam\", StringComparison.OrdinalIgnoreCase)");
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not locate repo file: " + string.Join("/", parts));
        }
    }
}
