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
        public async Task SelfKey_ReadsNotApplicableWithoutSelfGameData_AndIsAbsentWithoutIdentityLoader()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var rowWithoutSelfData = MakeRow("ACH_1", "friend1", selfHasGameData: false);
            var rowsWithoutSelfData = new List<FriendAchievementDisplayItem> { rowWithoutSelfData };

            var withLoader = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>(),
                loadCurrentUserIdentities: IdentityLoader("PersonaName"));
            await withLoader.SelfIdentityLoadTask;
            withLoader.UpdateRows(rowsWithoutSelfData, rowsWithoutSelfData);

            // The entry does not depend on the game: it is listed, and the rows read N/A.
            CollectionAssert.AreEqual(
                new[] { FriendVsFriendCompareController.SelfOptionKey },
                withLoader.OptionKeys.ToList());
            withLoader.SelectKey(FriendVsFriendCompareController.SelfOptionKey, true);
            Assert.IsTrue(rowWithoutSelfData.HasComparison);
            Assert.IsFalse(rowWithoutSelfData.ComparisonApplicable);

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

        [TestMethod]
        public void NoSelectedFriend_ComparesEveryOtherFriendsRows_PerGame()
        {
            var candidate = MakeFriend("friend2", "Friend Two");
            var candidateKey = FriendOverviewProjection.GetFriendScopeKey(candidate);
            var controller = new FriendVsFriendCompareController(
                () => null,
                () => new List<FriendSummaryItem> { candidate });

            FriendAchievementDisplayItem Row(string apiName, string friend, int appId)
            {
                var row = MakeRow(apiName, friend, unlocked: true);
                row.AppId = appId;
                row.FriendName = friend;
                return row;
            }

            // The same ApiName in two games: the candidate has it in game 1 only, and has no row
            // at all for game 3.
            var sharedGame = Row("ACH_1", "friend1", 1);
            var otherGame = Row("ACH_1", "friend1", 2);
            var lockedForCandidate = Row("ACH_2", "friend1", 1);
            var gameWithoutCandidate = Row("ACH_1", "friend1", 3);
            var candidateRow = Row("ACH_1", "friend2", 1);
            var candidateOtherGame = Row("ACH_9", "friend2", 2);
            var rows = new List<FriendAchievementDisplayItem>
            {
                sharedGame, otherGame, lockedForCandidate, gameWithoutCandidate, candidateRow, candidateOtherGame
            };
            controller.UpdateRows(rows, rows);

            Assert.IsTrue(controller.IsCompareAvailable);
            controller.SelectKey(candidateKey, true);

            Assert.IsTrue(sharedGame.ComparisonUnlocked);
            Assert.AreEqual("friend1", sharedGame.ComparisonOwnerName);
            Assert.IsTrue(otherGame.HasComparison);
            Assert.IsFalse(otherGame.ComparisonUnlocked, "the same ApiName in another game is not theirs");
            Assert.IsTrue(lockedForCandidate.HasComparison);
            Assert.IsFalse(lockedForCandidate.ComparisonUnlocked);
            Assert.IsTrue(gameWithoutCandidate.HasComparison);
            Assert.IsFalse(gameWithoutCandidate.ComparisonApplicable, "no data for the game reads N/A");
            Assert.IsTrue(lockedForCandidate.ComparisonApplicable, "locked in a game they have");
            Assert.IsFalse(candidateRow.HasComparison, "their own rows are not compared with themselves");
        }

        [TestMethod]
        public void CandidateWithoutDataForTheGame_ReadsNotApplicable_UnlessTheHostKnowsTheyHaveIt()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var candidate = MakeFriend("friend2", "Friend Two");
            var candidateKey = FriendOverviewProjection.GetFriendScopeKey(candidate);
            var row = MakeRow("ACH_1", "friend1", unlocked: true);
            var rows = new List<FriendAchievementDisplayItem> { row };

            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem> { candidate });
            controller.UpdateRows(rows, rows);
            controller.SelectKey(candidateKey, true);

            Assert.IsTrue(row.HasComparison);
            Assert.AreEqual("Friend Two", row.ComparisonFriendName);
            Assert.IsFalse(row.ComparisonApplicable);
            Assert.IsFalse(row.ComparisonUnlocked);

            // The host knows the friend owns the game with nothing unlocked: Locked, not N/A.
            var owning = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem> { candidate },
                hasGameData: (friend, item) => true);
            owning.UpdateRows(rows, rows);
            owning.SelectKey(candidateKey, true);

            Assert.IsTrue(row.ComparisonApplicable);
            Assert.IsFalse(row.ComparisonUnlocked);
        }

        [TestMethod]
        public void ScrapedAllLockedRows_ReadNotApplicable_UntilOwnershipIsKnown()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var candidate = MakeFriend("friend2", "Friend Two");
            var candidateKey = FriendOverviewProjection.GetFriendScopeKey(candidate);
            var row = MakeRow("ACH_1", "friend1", unlocked: true);

            // The candidate's achievements were scraped for the game: every row locked, and
            // nothing says they own it.
            var scraped = MakeRow("ACH_1", "friend2");
            var rows = new List<FriendAchievementDisplayItem> { row, scraped };
            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem> { candidate });
            controller.UpdateRows(rows, rows);
            controller.SelectKey(candidateKey, true);

            Assert.IsTrue(row.HasComparison);
            Assert.IsFalse(row.ComparisonApplicable);

            scraped.FriendOwnsGame = true;
            controller.UpdateRows(rows, rows);

            Assert.IsTrue(row.ComparisonApplicable, "a game they own reads Locked");
            Assert.IsFalse(row.ComparisonUnlocked);
        }

        [TestMethod]
        public void PersistSelection_RestoresTheRememberedFriend_AndSavesOnlyTheUsersPicks()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var candidate = MakeFriend("friend2", "Friend Two");
            var candidateKey = FriendOverviewProjection.GetFriendScopeKey(candidate);
            var candidates = new List<FriendSummaryItem>();
            var saves = new List<string>();
            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => candidates);
            var row = MakeRow("ACH_1", "friend1");
            var rows = new List<FriendAchievementDisplayItem> { row };

            // Not on offer for this pair: the dropdown still shows the remembered friend as its
            // selection, by the saved name, and draws no comparison.
            controller.PersistSelection(candidateKey + "\tFriend Two", saves.Add);
            controller.UpdateRows(rows, rows);
            Assert.IsTrue(controller.IsCompareAvailable);
            Assert.IsTrue(controller.IsKeySelected(candidateKey));
            Assert.AreEqual("Friend Two", controller.CompareSelectionText);
            CollectionAssert.Contains(controller.OptionKeys.ToList(), candidateKey);
            Assert.IsFalse(row.HasComparison);

            candidates.Add(candidate);
            controller.UpdateRows(rows, rows);
            Assert.IsTrue(row.HasComparison, "applied once on offer");
            Assert.AreEqual(1, controller.OptionKeys.Count(key => key == candidateKey));

            // The host clears the selection when the pair changes; the pick stays remembered.
            controller.ClearSelection();
            Assert.IsFalse(row.HasComparison);
            controller.UpdateRows(rows, rows);
            Assert.IsTrue(row.HasComparison);
            Assert.AreEqual(0, saves.Count);

            controller.SelectKey(candidateKey, false);
            CollectionAssert.AreEqual(new string[] { null }, saves);
            controller.UpdateRows(rows, rows);
            Assert.IsFalse(controller.IsKeySelected(candidateKey), "a cleared pick is forgotten");
            Assert.IsFalse(row.HasComparison);

            controller.SelectKey(candidateKey, true);
            CollectionAssert.AreEqual(new[] { null, candidateKey + "\tFriend Two" }, saves);
        }

        [TestMethod]
        public void RememberedEntryThatIsNotOnOffer_IsForgottenWhenUnchecked()
        {
            var selectedFriend = MakeFriend("friend1", "Friend One");
            var saves = new List<string>();
            var controller = new FriendVsFriendCompareController(
                () => selectedFriend,
                () => new List<FriendSummaryItem>());
            controller.PersistSelection("steam|friend9\tFriend Nine", saves.Add);

            controller.SelectKey("steam|friend9", false);

            CollectionAssert.AreEqual(new string[] { null }, saves);
            Assert.IsFalse(controller.IsCompareAvailable);
            Assert.IsFalse(controller.OptionKeys.Any());
        }
    }
}
