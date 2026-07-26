using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using System.Linq;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class FriendMergeSettingsTests
    {
        [TestMethod]
        public void AddOrUpdateFriendMergeGroup_NormalizesMembersAndEnforcesOneAccountPerProvider()
        {
            var settings = CreateSettingsWithFriends();

            var group = settings.AddOrUpdateFriendMergeGroup(
                new[]
                {
                    FriendAccountRef.From("Steam", "steam-a"),
                    FriendAccountRef.From("Steam", "steam-b"),
                    FriendAccountRef.From("Exophase", "exo-a")
                },
                "  Squad  ",
                FriendAccountRef.From("Steam", "steam-b"));

            Assert.IsNotNull(group);
            Assert.AreEqual("Squad", group.Nickname);
            Assert.AreEqual(2, group.Members.Count);
            Assert.AreEqual(1, group.Members.Count(member => member.ProviderKey == "Steam"));
            Assert.AreEqual(1, group.Members.Count(member => member.ProviderKey == "Exophase"));
            Assert.IsTrue(group.Members.Any(member => member.Matches("Steam", "steam-a")));
            Assert.IsFalse(group.Members.Any(member => member.Matches("Steam", "steam-b")));
            Assert.IsTrue(group.Members.Any(member => member.Matches(group.AvatarAccount.ProviderKey, group.AvatarAccount.ExternalUserId)));
        }

        [TestMethod]
        public void RemoveFriendMergeGroup_PreservesIndividualFriendSettings()
        {
            var settings = CreateSettingsWithFriends();
            settings.SetFriendNickname("Steam", "steam-a", "Steam Nick");
            var group = settings.AddOrUpdateFriendMergeGroup(
                new[]
                {
                    FriendAccountRef.From("Steam", "steam-a"),
                    FriendAccountRef.From("Exophase", "exo-a")
                },
                "Merged Nick");

            Assert.IsTrue(settings.RemoveFriendMergeGroup(group.Id));

            Assert.AreEqual(0, settings.GetFriendMergeGroups().Count);
            Assert.AreEqual("Steam Nick", settings.GetFriendSetting("Steam", "steam-a").Nickname);
            Assert.IsNotNull(settings.GetFriendSetting("Exophase", "exo-a"));
        }

        [TestMethod]
        public void RemoveFriendSetting_PrunesInvalidMergeGroup()
        {
            var settings = CreateSettingsWithFriends();
            settings.AddOrUpdateFriendMergeGroup(
                new[]
                {
                    FriendAccountRef.From("Steam", "steam-a"),
                    FriendAccountRef.From("Exophase", "exo-a")
                });

            Assert.IsTrue(settings.RemoveFriendSetting("Steam", "steam-a"));

            Assert.AreEqual(0, settings.GetFriendMergeGroups().Count);
            Assert.IsNotNull(settings.GetFriendSetting("Exophase", "exo-a"));
        }

        [TestMethod]
        public void Clone_DeepCopiesFriendMergeGroups()
        {
            var settings = CreateSettingsWithFriends();
            var group = settings.AddOrUpdateFriendMergeGroup(
                new[]
                {
                    FriendAccountRef.From("Steam", "steam-a"),
                    FriendAccountRef.From("Exophase", "exo-a")
                },
                "Merged Nick",
                FriendAccountRef.From("Exophase", "exo-a"));

            var clone = settings.Clone();
            clone.SetFriendMergeGroupNickname(group.Id, "Clone Nick");

            Assert.AreEqual("Merged Nick", settings.GetFriendMergeGroup(group.Id).Nickname);
            Assert.AreEqual("Clone Nick", clone.GetFriendMergeGroup(group.Id).Nickname);
        }

        private static PersistedSettings CreateSettingsWithFriends()
        {
            var settings = new PersistedSettings();
            settings.AddOrUpdateFriend("Steam", "steam-a", "Steam A", null, null, FriendSettingsSource.AutoDiscovered);
            settings.AddOrUpdateFriend("Steam", "steam-b", "Steam B", null, null, FriendSettingsSource.AutoDiscovered);
            settings.AddOrUpdateFriend(
                "Exophase",
                "exo-a",
                "Exo A",
                null,
                null,
                FriendSettingsSource.Manual,
                new[] { "steam", "psn" });
            return settings;
        }
    }
}
