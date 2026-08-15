using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Refresh;
using System.Collections.ObjectModel;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class FriendFullScanExclusionTests
    {
        [TestMethod]
        public void FriendSettingsEntry_Clone_PreservesExcludeFromFullScans()
        {
            var entry = new FriendSettingsEntry
            {
                ProviderKey = "Steam",
                ExternalUserId = "alice",
                ExcludeFromFullScans = true
            };

            Assert.IsTrue(entry.Clone().ExcludeFromFullScans);
        }

        [TestMethod]
        public void GetFullScanExcludedFriendIds_ReturnsOnlyFlaggedFriendsForProvider()
        {
            var settings = new PersistedSettings
            {
                Friends = new ObservableCollection<FriendSettingsEntry>
                {
                    new FriendSettingsEntry { ProviderKey = "Steam", ExternalUserId = "alice", ExcludeFromFullScans = true },
                    new FriendSettingsEntry { ProviderKey = "Steam", ExternalUserId = "bob" },
                    new FriendSettingsEntry { ProviderKey = "Exophase", ExternalUserId = "carol", ExcludeFromFullScans = true }
                }
            };

            var excluded = settings.GetFullScanExcludedFriendIds("Steam");

            Assert.IsTrue(excluded.Contains("alice"));
            Assert.IsFalse(excluded.Contains("bob"));
            Assert.IsFalse(excluded.Contains("carol"));
        }

        [TestMethod]
        public void HasExplicitFriendTargets_TrueForFriendAccountsOrExternalIds()
        {
            Assert.IsFalse(FriendRefreshWorkPolicy.HasExplicitFriendTargets(null));
            Assert.IsFalse(FriendRefreshWorkPolicy.HasExplicitFriendTargets(new FriendRefreshOptions()));
            Assert.IsFalse(FriendRefreshWorkPolicy.HasExplicitFriendTargets(new FriendRefreshOptions
            {
                FriendExternalUserIds = new[] { "  " }
            }));
            Assert.IsTrue(FriendRefreshWorkPolicy.HasExplicitFriendTargets(new FriendRefreshOptions
            {
                FriendAccounts = new[] { FriendAccountRef.From("Steam", "alice") }
            }));
            Assert.IsTrue(FriendRefreshWorkPolicy.HasExplicitFriendTargets(new FriendRefreshOptions
            {
                FriendExternalUserIds = new[] { "alice" }
            }));
        }
    }
}
