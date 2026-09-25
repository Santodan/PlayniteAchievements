using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseProfileLinkTests
    {
        private Func<string, string, string> _previousBuilder;
        private Func<IReadOnlyList<KeyValuePair<string, string>>> _previousNames;

        [TestInitialize]
        public void Initialize()
        {
            _previousBuilder = ShowcaseProfileResolver.ProfileUrlBuilder;
            _previousNames = ShowcaseProfileResolver.CurrentUserProfileNames;
            // Stand-ins for the providers' own patterns.
            ShowcaseProfileResolver.ProfileUrlBuilder = (key, user) =>
                string.Equals(key, "PSN", StringComparison.OrdinalIgnoreCase)
                    ? "https://psnprofiles.com/" + Uri.EscapeDataString(user)
                    : string.Equals(key, "Steam", StringComparison.OrdinalIgnoreCase)
                        ? "https://steamcommunity.com/profiles/" + user
                        : null;
            ShowcaseProfileResolver.CurrentUserProfileNames = () => new[]
            {
                new KeyValuePair<string, string>("Steam", "76561197960287930")
            };
        }

        [TestCleanup]
        public void Cleanup()
        {
            ShowcaseProfileResolver.ProfileUrlBuilder = _previousBuilder;
            ShowcaseProfileResolver.CurrentUserProfileNames = _previousNames;
        }

        [TestMethod]
        public void BuildLinkUrl_BuildsNamesThroughTheProviderAndKeepsFullLinks()
        {
            Assert.AreEqual(
                "https://psnprofiles.com/player%20one",
                ShowcaseProfileResolver.BuildLinkUrl("PSN", " player one "));
            Assert.AreEqual(
                "https://my.site/profile",
                ShowcaseProfileResolver.BuildLinkUrl("PSN", "my.site/profile"));
            Assert.AreEqual(
                "https://example.com/u/x",
                ShowcaseProfileResolver.BuildLinkUrl("Epic", "https://example.com/u/x"));
            Assert.IsNull(ShowcaseProfileResolver.BuildLinkUrl("Epic", "name-only"));
            Assert.IsNull(ShowcaseProfileResolver.BuildLinkUrl("PSN", "  "));
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
        public void ResolveLinks_UnsavedShowsEveryKnownCurrentUserName()
        {
            var links = ShowcaseProfileResolver.ResolveLinks(new ShowcaseProfileSettings());

            Assert.AreEqual(1, links.Count);
            Assert.AreEqual("Steam", links[0].ProviderKey);
            Assert.AreEqual("https://steamcommunity.com/profiles/76561197960287930", links[0].Url);
        }

        [TestMethod]
        public void ResolveLinks_SavedListIsExactInOrderAndBlankFallsBackToTheStoredName()
        {
            var manual = new ShowcaseProfileSettings
            {
                Links = new List<ShowcaseProfileLink>
                {
                    new ShowcaseProfileLink { ProviderKey = "PSN", Value = "player" },
                    new ShowcaseProfileLink { ProviderKey = "Epic", Value = "name-only" },
                    new ShowcaseProfileLink { ProviderKey = "Steam", Value = " " }
                }
            };

            var links = ShowcaseProfileResolver.ResolveLinks(manual);

            CollectionAssert.AreEqual(
                new[] { "PSN", "Steam" },
                links.Select(link => link.ProviderKey).ToArray());
            Assert.AreEqual("https://psnprofiles.com/player", links[0].Url);
            Assert.AreEqual("https://steamcommunity.com/profiles/76561197960287930", links[1].Url);
        }

        [TestMethod]
        public void ResolveLinks_SavedEmptyListShowsNone()
        {
            var manual = new ShowcaseProfileSettings { Links = new List<ShowcaseProfileLink>() };

            Assert.AreEqual(0, ShowcaseProfileResolver.ResolveLinks(manual).Count);
        }

        [TestMethod]
        public void Clone_CopiesLinksDeeplyAndKeepsUnsavedNull()
        {
            var profile = new ShowcaseProfileSettings
            {
                Links = new List<ShowcaseProfileLink>
                {
                    new ShowcaseProfileLink { ProviderKey = "PSN", Value = "a" }
                }
            };

            var clone = profile.Clone();
            clone.Links[0].Value = "b";

            Assert.AreEqual("a", profile.Links[0].Value);
            Assert.IsNull(new ShowcaseProfileSettings().Clone().Links);
        }
    }
}
