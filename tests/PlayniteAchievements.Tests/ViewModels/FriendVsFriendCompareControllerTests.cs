using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class FriendVsFriendCompareControllerTests
    {
        private static readonly DateTime SelfUnlockTime = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

        private static FriendSummaryItem MakeFriend(string externalUserId, string displayName)
        {
            return new FriendSummaryItem
            {
                ProviderKey = "Steam",
                ExternalUserId = externalUserId,
                DisplayName = displayName
            };
        }

        private static FriendAchievementDisplayItem MakeRow(
            string apiName,
            string friendExternalUserId,
            bool unlocked = false,
            bool unlockedBySelf = false,
            DateTime? selfUnlockTimeUtc = null,
            bool selfHasGameData = true)
        {
            return new FriendAchievementDisplayItem
            {
                ApiName = apiName,
                ProviderKey = "Steam",
                FriendExternalUserId = friendExternalUserId,
                Unlocked = unlocked,
                UnlockedBySelf = unlockedBySelf,
                SelfUnlockTimeUtc = selfUnlockTimeUtc,
                SelfHasGameData = selfHasGameData
            };
        }

        private static Func<IReadOnlyList<FriendIdentity>> IdentityLoader(
            string displayName,
            string externalUserId = "76561198000000000")
        {
            return () => new List<FriendIdentity>
            {
                new FriendIdentity
                {
                    ProviderKey = "Steam",
                    ExternalUserId = externalUserId,
                    DisplayName = displayName,
                    AvatarPath = @"C:\avatars\me.png"
                }
            };
        }

        [TestMethod]
        public async Task OptionKeys_PrependSelfKey_WhenSelfDataAvailable()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var candidate = MakeFriend("friend2", "Friend Two");
            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem> { candidate },
                loadCurrentUserIdentities: IdentityLoader("PersonaName"));
            await controller.SelfIdentityLoadTask;

            var rows = new List<FriendAchievementDisplayItem> { MakeRow("ACH_1", "friend1") };
            controller.UpdateRows(rows, rows);

            var keys = controller.OptionKeys.ToList();
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual(FriendVsFriendCompareController.SelfOptionKey, keys[0]);
            Assert.AreEqual(FriendOverviewProjection.GetFriendScopeKey(candidate), keys[1]);
            Assert.IsTrue(controller.IsCompareAvailable);
        }

        [TestMethod]
        public async Task IsCompareAvailable_TrueWithSelfOnly_WhenNoFriendCandidates()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>(),
                loadCurrentUserIdentities: IdentityLoader("PersonaName"));
            await controller.SelfIdentityLoadTask;

            var rows = new List<FriendAchievementDisplayItem> { MakeRow("ACH_1", "friend1") };
            controller.UpdateRows(rows, rows);

            Assert.IsTrue(controller.IsCompareAvailable);
            CollectionAssert.AreEqual(
                new[] { FriendVsFriendCompareController.SelfOptionKey },
                controller.OptionKeys.ToList());
        }

        [TestMethod]
        public async Task SelfKey_Absent_WhenNoSelfGameDataOrNoIdentityLoader()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var rowsWithoutSelfData = new List<FriendAchievementDisplayItem>
            {
                MakeRow("ACH_1", "friend1", selfHasGameData: false)
            };

            var withLoader = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>(),
                loadCurrentUserIdentities: IdentityLoader("PersonaName"));
            await withLoader.SelfIdentityLoadTask;
            withLoader.UpdateRows(rowsWithoutSelfData, rowsWithoutSelfData);

            Assert.IsFalse(withLoader.OptionKeys.Any());
            Assert.IsFalse(withLoader.IsCompareAvailable);

            var withoutLoader = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>());
            var rowsWithSelfData = new List<FriendAchievementDisplayItem> { MakeRow("ACH_1", "friend1") };
            withoutLoader.UpdateRows(rowsWithSelfData, rowsWithSelfData);

            Assert.IsFalse(withoutLoader.OptionKeys.Any());
        }

        [TestMethod]
        public async Task SelectSelfKey_AppliesAndClearsSelfComparison()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var candidate = MakeFriend("friend2", "Friend Two");
            var candidateKey = FriendOverviewProjection.GetFriendScopeKey(candidate);
            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem> { candidate },
                loadCurrentUserIdentities: IdentityLoader("PersonaName"));
            await controller.SelfIdentityLoadTask;

            var selfUnlockedRow = MakeRow(
                "ACH_1", "friend1", unlockedBySelf: true, selfUnlockTimeUtc: SelfUnlockTime);
            var selfLockedRow = MakeRow("ACH_2", "friend1");
            var candidateRow = MakeRow("ACH_1", "friend2", unlocked: true);
            var rows = new List<FriendAchievementDisplayItem>
            {
                selfUnlockedRow, selfLockedRow, candidateRow
            };
            controller.UpdateRows(rows, rows);

            controller.SelectKey(FriendVsFriendCompareController.SelfOptionKey, true);

            Assert.IsTrue(controller.IsKeySelected(FriendVsFriendCompareController.SelfOptionKey));
            Assert.IsTrue(selfUnlockedRow.HasComparison);
            Assert.AreEqual("PersonaName", selfUnlockedRow.ComparisonFriendName);
            Assert.IsTrue(selfUnlockedRow.ComparisonUnlocked);
            Assert.AreEqual(SelfUnlockTime, selfUnlockedRow.ComparisonUnlockTimeUtc);
            Assert.AreEqual("Friend One", selfUnlockedRow.ComparisonOwnerName);
            Assert.IsTrue(selfLockedRow.HasComparison);
            Assert.IsFalse(selfLockedRow.ComparisonUnlocked);
            Assert.IsFalse(candidateRow.HasComparison);

            controller.SelectKey(candidateKey, true);

            Assert.IsFalse(controller.IsKeySelected(FriendVsFriendCompareController.SelfOptionKey));
            Assert.IsTrue(controller.IsKeySelected(candidateKey));
            Assert.AreEqual("Friend Two", selfUnlockedRow.ComparisonFriendName);

            controller.ClearSelection();

            Assert.IsFalse(selfUnlockedRow.HasComparison);
            Assert.IsFalse(selfLockedRow.HasComparison);
        }

        [TestMethod]
        public async Task GetDisplayNameForKey_SelfLabel_UsernameOrFallback()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var rows = new List<FriendAchievementDisplayItem> { MakeRow("ACH_1", "friend1") };

            var named = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>(),
                loadCurrentUserIdentities: IdentityLoader("PersonaName"));
            await named.SelfIdentityLoadTask;
            named.UpdateRows(rows, rows);
            Assert.AreEqual(
                "PersonaName",
                named.GetDisplayNameForKey(FriendVsFriendCompareController.SelfOptionKey));

            var unmapped = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>(),
                loadCurrentUserIdentities: IdentityLoader("unmapped"));
            await unmapped.SelfIdentityLoadTask;
            unmapped.UpdateRows(rows, rows);
            Assert.AreEqual(
                "Me",
                unmapped.GetDisplayNameForKey(FriendVsFriendCompareController.SelfOptionKey));

            // A display name equal to the external id is a raw account id, not a username.
            var rawId = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>(),
                loadCurrentUserIdentities: IdentityLoader("76561198000000000"));
            await rawId.SelfIdentityLoadTask;
            rawId.UpdateRows(rows, rows);
            Assert.AreEqual(
                "Me",
                rawId.GetDisplayNameForKey(FriendVsFriendCompareController.SelfOptionKey));
        }
    }
}
