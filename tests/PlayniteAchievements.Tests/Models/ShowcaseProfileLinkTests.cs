using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseProfileLinkTests
    {
        [TestMethod]
        public void DeriveProfileUrl_BuildsKnownProviderPages()
        {
            Assert.AreEqual(
                "https://steamcommunity.com/profiles/76561197960287930",
                ShowcaseProfileResolver.DeriveProfileUrl("Steam", "76561197960287930"));
            Assert.AreEqual(
                "https://retroachievements.org/user/Some%20User",
                ShowcaseProfileResolver.DeriveProfileUrl("RetroAchievements", "Some User"));
            Assert.AreEqual(
                "https://www.exophase.com/user/player/",
                ShowcaseProfileResolver.DeriveProfileUrl("Exophase", " player "));
        }

        [TestMethod]
        public void DeriveProfileUrl_NullForUnknownProvidersAndNonNumericSteamIds()
        {
            Assert.IsNull(ShowcaseProfileResolver.DeriveProfileUrl("PSN", "someone"));
            Assert.IsNull(ShowcaseProfileResolver.DeriveProfileUrl("Steam", "vanity"));
            Assert.IsNull(ShowcaseProfileResolver.DeriveProfileUrl("Steam", " "));
        }

        [TestMethod]
        public void NormalizeUrl_AddsHttpsAndRejectsOtherSchemes()
        {
            Assert.AreEqual(
                "https://psnprofiles.com/player",
                ShowcaseProfileResolver.NormalizeUrl("psnprofiles.com/player"));
            Assert.AreEqual(
                "http://example.com/",
                ShowcaseProfileResolver.NormalizeUrl("http://example.com"));
            Assert.IsNull(ShowcaseProfileResolver.NormalizeUrl("file:///C:/Windows/notepad.exe"));
            Assert.IsNull(ShowcaseProfileResolver.NormalizeUrl("  "));
        }

        [TestMethod]
        public void ResolveLinks_ManualEntriesOverrideHideAndOrderBeforeDerived()
        {
            var identities = new List<FriendIdentity>
            {
                new FriendIdentity { ProviderKey = "Steam", ExternalUserId = "76561197960287930" },
                new FriendIdentity { ProviderKey = "RetroAchievements", ExternalUserId = "ra_user" },
                new FriendIdentity { ProviderKey = "Exophase", ExternalUserId = "exo_user" }
            };
            var manual = new ShowcaseProfileSettings
            {
                Links = new List<ShowcaseProfileLink>
                {
                    new ShowcaseProfileLink { ProviderKey = "PSN", Url = "psnprofiles.com/player" },
                    new ShowcaseProfileLink { ProviderKey = "RetroAchievements", Hidden = true },
                    new ShowcaseProfileLink { ProviderKey = "Exophase", Url = " " }
                }
            };

            var links = ShowcaseProfileResolver.ResolveLinks(manual, identities);

            CollectionAssert.AreEqual(
                new[] { "PSN", "Exophase", "Steam" },
                links.Select(link => link.ProviderKey).ToArray());
            Assert.AreEqual("https://psnprofiles.com/player", links[0].Url);
            Assert.AreEqual("https://www.exophase.com/user/exo_user/", links[1].Url);
            Assert.AreEqual("https://steamcommunity.com/profiles/76561197960287930", links[2].Url);
        }

        [TestMethod]
        public void Clone_CopiesLinksDeeply()
        {
            var profile = new ShowcaseProfileSettings
            {
                Links = new List<ShowcaseProfileLink>
                {
                    new ShowcaseProfileLink { ProviderKey = "PSN", Url = "a", Hidden = true }
                }
            };

            var clone = profile.Clone();
            clone.Links[0].Url = "b";

            Assert.AreEqual("a", profile.Links[0].Url);
            Assert.IsTrue(clone.Links[0].Hidden);
        }
    }
}
