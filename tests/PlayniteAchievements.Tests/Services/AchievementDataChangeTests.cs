using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class AchievementDataChangeTests
    {
        private static readonly Guid GameA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid GameB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

        private static readonly AchievementQuery[] EveryShape =
        {
            AchievementQuery.CurrentUser,
            AchievementQuery.CurrentUser.ForGames(new[] { GameB }),
            AchievementQuery.CurrentUser.ForProviderGame("Steam", 10, null),
            AchievementQuery.AllFriends,
            AchievementQuery.AllFriends.ForFriend("Steam"),
            AchievementQuery.AllFriends.ForFriend("Steam", "f1"),
            AchievementQuery.AllFriends.ForGames(new[] { GameB }),
            AchievementQuery.AllFriends.ForFriend("Steam", "f1").ForProviderGame("Steam", 10, "k10")
        };

        [TestMethod]
        public void Full_TouchesEveryQuery()
        {
            foreach (var query in EveryShape)
            {
                Assert.IsTrue(AchievementDataChange.Full.Touches(query));
            }

            Assert.IsTrue(AchievementDataChange.Of(CacheInvalidatedEventArgs.FullInvalidation, null).IsFull);
            Assert.IsTrue(AchievementDataChange.ForGames(Enumerable.Range(0, 40).Select(_ => Guid.NewGuid())).IsFull,
                "more games than a scoped invalidation holds is full");
        }

        [TestMethod]
        public void Games_TouchQueriesOfAllGamesOrIncludingOne_ForTheUserAndFriends()
        {
            var change = AchievementDataChange.ForGames(new[] { GameA });

            Assert.IsTrue(change.Touches(AchievementQuery.CurrentUser));
            Assert.IsTrue(change.Touches(AchievementQuery.CurrentUser.ForGames(new[] { GameA, GameB })));
            Assert.IsFalse(change.Touches(AchievementQuery.CurrentUser.ForGames(new[] { GameB })));
            Assert.IsFalse(change.Touches(AchievementQuery.CurrentUser.ForGames(new Guid[0])));
            Assert.IsTrue(change.Touches(AchievementQuery.CurrentUser.ForProviderGame("Steam", 10, null)),
                "a provider game's Playnite id is not known to the scope");

            // Friend rows carry the user's own state and the custom layer of the game.
            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends));
            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f1").ForGames(new[] { GameA })));
            Assert.IsFalse(change.Touches(AchievementQuery.AllFriends.ForGames(new[] { GameB })));
        }

        [TestMethod]
        public void AllFriends_TouchesFriendQueriesOnly()
        {
            var change = AchievementDataChange.Of(null, FriendCacheInvalidatedEventArgs.FullInvalidation);

            Assert.IsFalse(change.IsFull);
            Assert.IsTrue(change.AllFriends);
            foreach (var query in EveryShape)
            {
                Assert.AreEqual(query.Friends, change.Touches(query));
            }
        }

        [TestMethod]
        public void Roster_TouchesFriendQueriesOfTheProvider()
        {
            var change = Friends(FriendCacheChange.ForRoster("Steam"));

            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends));
            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForFriend("steam", "f9")));
            Assert.IsFalse(change.Touches(AchievementQuery.AllFriends.ForFriend("Xbox")));
            Assert.IsFalse(change.Touches(AchievementQuery.CurrentUser));
        }

        [TestMethod]
        public void OwnershipAndRemoval_TouchQueriesOfTheFriend_WhateverTheGame()
        {
            foreach (var change in new[]
                     {
                         Friends(FriendCacheChange.ForFriendOwnership("Steam", "f1")),
                         Friends(FriendCacheChange.ForFriendRemoved("Steam", "f1"))
                     })
            {
                Assert.IsTrue(change.Touches(AchievementQuery.AllFriends));
                Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam")));
                Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "F1").ForGames(new[] { GameB })));
                Assert.IsFalse(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f2")));
                Assert.IsFalse(change.Touches(AchievementQuery.AllFriends.ForFriend("Xbox")));
                Assert.IsFalse(change.Touches(AchievementQuery.CurrentUser));
            }
        }

        [TestMethod]
        public void GameDefinition_TouchesFriendQueriesOfTheProviderGame()
        {
            var byAppId = Friends(FriendCacheChange.ForGameDefinition("Steam", 10, null));
            var byKey = Friends(FriendCacheChange.ForGameDefinition("Steam", 0, "k10"));
            var unknown = Friends(FriendCacheChange.ForGameDefinition("Steam", 0, null));
            var game = AchievementQuery.AllFriends.ForProviderGame("Steam", 10, "k10");
            var otherGame = AchievementQuery.AllFriends.ForProviderGame("Steam", 11, "k11");

            Assert.IsTrue(byAppId.Touches(game));
            Assert.IsTrue(byKey.Touches(game));
            Assert.IsTrue(unknown.Touches(otherGame), "a change without a game identity touches every game of the provider");
            Assert.IsFalse(byAppId.Touches(otherGame));
            Assert.IsFalse(byKey.Touches(otherGame));
            Assert.IsFalse(byAppId.Touches(AchievementQuery.AllFriends.ForProviderGame("Xbox", 10, "k10")));
            Assert.IsFalse(byAppId.Touches(AchievementQuery.AllFriends.ForFriend("Xbox")));
            Assert.IsTrue(byAppId.Touches(AchievementQuery.AllFriends));
            Assert.IsTrue(byAppId.Touches(AchievementQuery.AllFriends.ForGames(new[] { GameB })),
                "a Playnite game narrowing cannot be matched to a provider identity");
            Assert.IsFalse(byAppId.Touches(AchievementQuery.CurrentUser));
        }

        [TestMethod]
        public void FriendGameAchievements_TouchQueriesOfTheFriendAndGame()
        {
            var change = Friends(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 10, "k10"));

            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends));
            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f1")));
            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForProviderGame("Steam", 10, null)));
            Assert.IsTrue(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f1").ForProviderGame("Steam", 10, "k10")));
            Assert.IsFalse(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f2")));
            Assert.IsFalse(change.Touches(AchievementQuery.AllFriends.ForFriend("Steam", "f1").ForProviderGame("Steam", 11, "k11")));
            Assert.IsFalse(change.Touches(AchievementQuery.CurrentUser));
        }

        [TestMethod]
        public void Of_CarriesBothScopes()
        {
            var change = AchievementDataChange.Of(
                CacheInvalidatedEventArgs.Scoped(new[] { GameA, GameA, Guid.Empty }),
                FriendCacheInvalidatedEventArgs.Scoped(new[] { FriendCacheChange.ForRoster("Steam") }));

            Assert.IsFalse(change.IsFull);
            CollectionAssert.AreEqual(new[] { GameA }, change.GameIds.ToArray());
            Assert.AreEqual(FriendCacheChangeKind.Roster, change.FriendChanges.Single().Kind);
            Assert.IsFalse(change.AllFriends);
        }

        [TestMethod]
        public void NamedAccounts_AreTouchedByTheirOwnAndTheCompareAccountsChanges()
        {
            var query = AchievementQuery.AllFriends
                .ForFriendAccounts(new[] { Models.Settings.FriendAccountRef.From("Steam", "f1"), Models.Settings.FriendAccountRef.From("Xbox", "x1") })
                .CompareWith(new[] { Models.Settings.FriendAccountRef.From("Steam", "f2") });

            Assert.IsTrue(Friends(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 10, null)).Touches(query));
            Assert.IsTrue(Friends(FriendCacheChange.ForFriendGameAchievements("Xbox", "x1", 10, null)).Touches(query), "a merged friend's other account");
            Assert.IsTrue(Friends(FriendCacheChange.ForFriendOwnership("Steam", "f2")).Touches(query), "the compare side's ownership");
            Assert.IsFalse(Friends(FriendCacheChange.ForFriendGameAchievements("Steam", "f3", 10, null)).Touches(query));
            Assert.IsFalse(Friends(FriendCacheChange.ForRoster("GOG")).Touches(query), "a roster of a provider none of them is on");
            Assert.IsTrue(Friends(FriendCacheChange.ForRoster("Xbox")).Touches(query));

            var none = AchievementQuery.AllFriends.ForFriendAccounts(new Models.Settings.FriendAccountRef[0]);
            Assert.IsFalse(Friends(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 10, null)).Touches(none));

            var game = query.ForProviderGame("Steam", 10, null);
            Assert.IsFalse(Friends(FriendCacheChange.ForFriendGameAchievements("Steam", "f1", 20, null)).Touches(game), "another game of theirs");
        }

        private static AchievementDataChange Friends(FriendCacheChange change) =>
            AchievementDataChange.Of(null, FriendCacheInvalidatedEventArgs.Scoped(new[] { change }));
    }
}
