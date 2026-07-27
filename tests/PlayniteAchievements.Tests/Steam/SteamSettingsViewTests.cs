using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Steam;

namespace PlayniteAchievements.Tests.Steam
{
    [TestClass]
    public class SteamSettingsViewTests
    {
        [TestMethod]
        public void BuildFriendListItems_UsesAvatarPathForActiveFriends()
        {
            var settings = new SteamSettings();

            var rows = SteamFriendListBuilder.BuildItems(
                settings,
                new[]
                {
                    new FriendIdentity
                    {
                        ExternalUserId = "111",
                        DisplayName = "Active Friend",
                        AvatarUrl = "active-avatar",
                        AvatarPath = "active-avatar-path"
                    }
                });

            var activeRow = rows.Single(row => row.SteamId == "111");
            Assert.AreEqual("active-avatar-path", activeRow.AvatarUrl);
            Assert.IsFalse(activeRow.IsIgnored);
        }

        [TestMethod]
        public void BuildFriendListItems_IgnoredFriendOverridesActiveCacheRow()
        {
            var settings = new SteamSettings();
            settings.AddIgnoredFriend("111", "Ignored Friend", "ignored-avatar");

            var rows = SteamFriendListBuilder.BuildItems(
                settings,
                new[]
                {
                    new FriendIdentity
                    {
                        ExternalUserId = "111",
                        DisplayName = "Active Friend",
                        AvatarUrl = "active-avatar"
                    }
                });

            var row = rows.Single();
            Assert.AreEqual("111", row.SteamId);
            Assert.AreEqual("Ignored Friend", row.DisplayName);
            Assert.AreEqual("ignored-avatar", row.AvatarUrl);
            Assert.IsTrue(row.IsIgnored);
        }
    }
}
