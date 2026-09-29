using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ShowcaseProfileResolverTests
    {
        [TestMethod]
        public void Resolve_PrefersSteamIdentityOverOtherProviders()
        {
            var identities = new List<FriendIdentity>
            {
                new FriendIdentity { ProviderKey = "RetroAchievements", DisplayName = "RetroName", AvatarPath = @"C:\ra.png" },
                new FriendIdentity { ProviderKey = "steam", DisplayName = "SteamName", AvatarPath = @"C:\steam.png" }
            };

            var resolved = ShowcaseProfileResolver.Resolve(null, identities);

            Assert.AreEqual("SteamName", resolved.DisplayName);
            Assert.AreEqual(@"C:\steam.png", resolved.AvatarPath);
            Assert.IsTrue(resolved.FromProviderIdentity);
        }

        [TestMethod]
        public void Resolve_FallsBackToFirstUsableIdentityWhenSteamAbsent()
        {
            var identities = new List<FriendIdentity>
            {
                new FriendIdentity { ProviderKey = "Exophase" },
                new FriendIdentity { ProviderKey = "RetroAchievements", ProviderNickname = "RetroNick" }
            };

            var resolved = ShowcaseProfileResolver.Resolve(null, identities);

            Assert.AreEqual("RetroNick", resolved.DisplayName);
        }

        [TestMethod]
        public void Resolve_ManualFieldsWinPerField()
        {
            var identities = new List<FriendIdentity>
            {
                new FriendIdentity { ProviderKey = "Steam", DisplayName = "SteamName", AvatarPath = @"C:\steam.png" }
            };
            var manual = new ShowcaseProfileSettings
            {
                DisplayName = "  Custom  ",
                Subtitle = "Completionist",
                BackgroundPath = @"C:\bg.png"
            };

            var resolved = ShowcaseProfileResolver.Resolve(manual, identities);

            Assert.AreEqual("Custom", resolved.DisplayName);
            Assert.AreEqual("Completionist", resolved.Subtitle);
            Assert.AreEqual(@"C:\steam.png", resolved.AvatarPath);
            Assert.AreEqual(@"C:\bg.png", resolved.BackgroundPath);
            Assert.IsFalse(resolved.FromProviderIdentity);
        }

        [TestMethod]
        public void Resolve_AllEmptyYieldsNullFields()
        {
            var resolved = ShowcaseProfileResolver.Resolve(
                new ShowcaseProfileSettings { DisplayName = "   " },
                new List<FriendIdentity>());

            Assert.IsNull(resolved.DisplayName);
            Assert.IsNull(resolved.Subtitle);
            Assert.IsNull(resolved.AvatarPath);
            Assert.IsNull(resolved.BackgroundPath);
            Assert.IsFalse(resolved.FromProviderIdentity);
        }

        [TestMethod]
        public void Resolve_PrefersAvatarPathOverAvatarUrl()
        {
            var identities = new List<FriendIdentity>
            {
                new FriendIdentity
                {
                    ProviderKey = "Steam",
                    DisplayName = "SteamName",
                    AvatarUrl = "https://example/avatar.jpg",
                    AvatarPath = @"C:\cached.jpg"
                }
            };

            var resolved = ShowcaseProfileResolver.Resolve(null, identities);

            Assert.AreEqual(@"C:\cached.jpg", resolved.AvatarPath);
        }
    }
}
