using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class FriendOverviewProjectionTests
    {
        [TestMethod]
        public void SelectedFriendGames_UsesFriendLinksAndUniqueUnlocks()
        {
            var gameId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var friend = new FriendSummaryItem
            {
                ProviderKey = "Steam",
                ExternalUserId = "alice",
                DisplayName = "Alice"
            };
            var game = new FriendGameSummaryItem
            {
                ProviderKey = "Steam",
                AppId = 10,
                PlayniteGameId = gameId,
                GameName = "Game One",
                TotalAchievements = 4
            };
            var data = new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem> { friend },
                Games = new List<FriendGameSummaryItem> { game },
                AllUnlockedAchievements = new List<FriendAchievementDisplayItem>
                {
                    Achievement("Steam", "alice", 10, gameId, "Game One", "Duplicate", Utc(2026, 1, 1), RarityTier.Common),
                    Achievement("Steam", "alice", 10, gameId, "Game One", "Duplicate", Utc(2026, 1, 3), RarityTier.Rare),
                    Achievement("Steam", "alice", 10, gameId, "Game One", "Unique", Utc(2026, 1, 2), RarityTier.UltraRare)
                },
                FriendGameLinks = new List<FriendGameLinkItem>
                {
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 10,
                        PlayniteGameId = gameId,
                        PlaytimeForeverMinutes = 90,
                        LastPlayedUtc = Utc(2026, 1, 4)
                    }
                }
            };

            var projection = new FriendOverviewProjection(data);
            var selectedRow = projection.GetSelectedFriendGames(friend).Single();

            Assert.AreNotSame(game, selectedRow);
            Assert.AreEqual(2, selectedRow.UnlockedAchievements);
            Assert.AreEqual(2, selectedRow.UniqueFriendUnlockedAchievementsCount);
            Assert.AreEqual(4, selectedRow.TotalAchievements);
            Assert.AreEqual(50, selectedRow.Progression);
            Assert.AreEqual(90UL * 60UL, selectedRow.PlaytimeSeconds);
            Assert.AreEqual(Utc(2026, 1, 4), selectedRow.LastPlayed);
            Assert.AreEqual(Utc(2026, 1, 3), selectedRow.LastUnlockUtc);
            Assert.AreEqual(90, selectedRow.TotalFriendPlaytimeMinutes);
            Assert.IsTrue(projection.HasFriendGamePairData(friend, game));
        }

        [TestMethod]
        public void SelectedFriendGames_IncludeOwnershipOnlyLinks()
        {
            var gameId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var friend = new FriendSummaryItem
            {
                ProviderKey = "Steam",
                ExternalUserId = "alice",
                DisplayName = "Alice"
            };
            var game = new FriendGameSummaryItem
            {
                ProviderKey = "Steam",
                AppId = 20,
                PlayniteGameId = gameId,
                GameName = "Owned Only",
                FriendCount = 1,
                TotalAchievements = 12
            };
            var data = new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem> { friend },
                Games = new List<FriendGameSummaryItem> { game },
                FriendGameLinks = new List<FriendGameLinkItem>
                {
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "alice",
                        AppId = 20,
                        PlayniteGameId = gameId,
                        PlaytimeForeverMinutes = 45
                    }
                }
            };

            var projection = new FriendOverviewProjection(data);
            var selectedRow = projection.GetSelectedFriendGames(friend).Single();

            // A shared library game the friend never played still gets a per-friend row (0/N),
            // carrying ownership-derived playtime.
            Assert.AreEqual(0, selectedRow.UnlockedAchievements);
            Assert.AreEqual(12, selectedRow.TotalAchievements);
            Assert.AreEqual(45UL * 60UL, selectedRow.PlaytimeSeconds);
            Assert.IsTrue(projection.HasFriendGamePairData(friend, game));
        }

        [TestMethod]
        public void PairData_RequiresUnlocksOrOwnership_LockedRowsDoNotCount()
        {
            var friend = new FriendSummaryItem
            {
                ProviderKey = "Steam",
                ExternalUserId = "alice",
                DisplayName = "Alice"
            };
            // Provider-only game (no PlayniteGameId): the friend has only locked rows and no
            // ownership link, matching the cache invariant that provider-only ownership is only
            // persisted once the friend has unlocks.
            var game = new FriendGameSummaryItem
            {
                ProviderKey = "Steam",
                AppId = 30,
                GameName = "Unowned Locked Only",
                TotalAchievements = 5
            };
            var locked = Achievement("Steam", "alice", 30, Guid.Empty, "Unowned Locked Only", "LockedOne", Utc(2026, 1, 1), RarityTier.Common);
            locked.Unlocked = false;
            locked.UnlockTimeUtc = null;
            locked.PlayniteGameId = null;
            var data = new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem> { friend },
                Games = new List<FriendGameSummaryItem> { game },
                AllAchievements = new List<FriendAchievementDisplayItem> { locked }
            };

            var projection = new FriendOverviewProjection(data);

            Assert.IsFalse(projection.HasFriendGamePairData(friend, game));
        }

        [TestMethod]
        public void MergeGroups_ProjectOneFriendWithCombinedSelectedGamesAndMergedScope()
        {
            var settings = CreateMergeSettings(groupNickname: "Alice Unified", avatarProviderKey: "Exophase");
            var group = settings.GetFriendMergeGroups().Single();
            var steamGameId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var exophaseGameId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var data = new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem>
                {
                    new FriendSummaryItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "steam-alice",
                        DisplayName = "Steam Alice",
                        AvatarPath = "steam-avatar.png",
                        SharedGamesCount = 1,
                        UnlockedAchievementsCount = 1,
                        CompletedGamesCount = 1,
                        CommonCount = 3,
                        RareCount = 1,
                        TrophyGoldCount = 2
                    },
                    new FriendSummaryItem
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-alice",
                        DisplayName = "Exo Alice",
                        AvatarPath = "exo-avatar.png",
                        SharedGamesCount = 1,
                        UnlockedAchievementsCount = 1,
                        CompletedGamesCount = 2,
                        UncommonCount = 2,
                        UltraRareCount = 1,
                        TrophyGoldCount = 1
                    }
                },
                Games = new List<FriendGameSummaryItem>
                {
                    new FriendGameSummaryItem
                    {
                        ProviderKey = "Steam",
                        AppId = 10,
                        PlayniteGameId = steamGameId,
                        GameName = "Steam Game",
                        TotalAchievements = 1
                    },
                    new FriendGameSummaryItem
                    {
                        ProviderKey = "Exophase",
                        AppId = 20,
                        PlayniteGameId = exophaseGameId,
                        GameName = "Exo Game",
                        TotalAchievements = 1
                    }
                },
                AllUnlockedAchievements = new List<FriendAchievementDisplayItem>
                {
                    Achievement("Steam", "steam-alice", 10, steamGameId, "Steam Game", "SteamA", Utc(2026, 1, 1), RarityTier.Common),
                    Achievement("Exophase", "exo-alice", 20, exophaseGameId, "Exo Game", "ExoA", Utc(2026, 1, 2), RarityTier.Rare)
                },
                FriendGameLinks = new List<FriendGameLinkItem>
                {
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "steam-alice",
                        AppId = 10,
                        PlayniteGameId = steamGameId,
                        PlaytimeForeverMinutes = 10
                    },
                    new FriendGameLinkItem
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-alice",
                        AppId = 20,
                        PlayniteGameId = exophaseGameId,
                        PlaytimeForeverMinutes = 20
                    }
                }
            };

            var projection = new FriendOverviewProjection(data, settings);
            var merged = projection.Friends.Single();
            var selectedGames = projection.GetSelectedFriendGames(merged);

            Assert.IsTrue(merged.IsMergedFriend);
            Assert.AreEqual(group.Id, merged.MergedFriendId);
            Assert.AreEqual("Alice Unified", merged.DisplayName);
            Assert.AreEqual("exo-avatar.png", merged.AvatarPath);
            CollectionAssert.AreEquivalent(new[] { "Steam", "Exophase" }, merged.MemberProviderKeys);
            Assert.AreEqual(FriendOverviewProjection.BuildFriendKey(FriendOverviewProjection.MergedProviderKey, group.Id), merged.FriendScopeKey);
            Assert.AreEqual(2, selectedGames.Count);
            Assert.IsTrue(selectedGames.Any(game => game.ProviderKey == "Steam" && game.GameName == "Steam Game"));
            Assert.IsTrue(selectedGames.Any(game => game.ProviderKey == "Exophase" && game.GameName == "Exo Game"));
            Assert.IsTrue(projection.HasFriendGamePairData(merged, data.Games[0]));
            Assert.IsTrue(projection.HasFriendGamePairData(merged, data.Games[1]));
            Assert.IsTrue(projection.AllAchievements.All(achievement => achievement.FriendGroupId == group.Id));
            Assert.IsTrue(projection.AllAchievements.All(achievement => achievement.FriendName == "Alice Unified"));

            // Merged friend sums member rarity/trophy counts; theme getters derive from them.
            Assert.AreEqual(3, merged.CommonCount);
            Assert.AreEqual(2, merged.UncommonCount);
            Assert.AreEqual(1, merged.RareCount);
            Assert.AreEqual(1, merged.UltraRareCount);
            Assert.AreEqual(3, merged.TrophyGoldCount);
            Assert.AreEqual(1, merged.TotalRare.Unlocked);
            Assert.AreEqual(2, merged.TotalRareAndUltraRare.Unlocked);
            Assert.AreEqual(7, merged.TotalOverall.Unlocked);
            Assert.AreEqual("7 / 7", merged.TotalOverall.Stats);
            Assert.AreEqual(3, merged.GoldTrophies);
            Assert.AreEqual(3, merged.TotalTrophies);
            Assert.AreEqual(3, merged.CompletedGamesCount);
            Assert.AreEqual(3, merged.CompletedGames);
        }

        [TestMethod]
        public void MergeGroups_FallBackToMemberNicknameAndFirstAvailableAvatar()
        {
            var settings = CreateMergeSettings(groupNickname: null, avatarProviderKey: null);
            settings.SetFriendNickname("Exophase", "exo-alice", "Exo Nick");
            var data = new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem>
                {
                    new FriendSummaryItem
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "steam-alice",
                        DisplayName = "Steam Alice"
                    },
                    new FriendSummaryItem
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-alice",
                        DisplayName = "Exo Alice",
                        AvatarPath = "exo-avatar.png"
                    }
                }
            };

            var projection = new FriendOverviewProjection(data, settings);
            var merged = projection.Friends.Single();

            Assert.AreEqual("Exo Nick", merged.DisplayName);
            Assert.AreEqual("exo-avatar.png", merged.AvatarPath);
        }

        [TestMethod]
        public void ApplyMergeIdentity_StampsFreshRowsWithoutFriendSummaries()
        {
            var settings = CreateMergeSettings(groupNickname: "Alice Unified", avatarProviderKey: null);
            var group = settings.GetFriendMergeGroups().Single();
            var gameId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            var unlockedMember = Achievement("Steam", "steam-alice", 10, gameId, "Steam Game", "SteamA", Utc(2026, 1, 1), RarityTier.Common);
            unlockedMember.FriendName = "Steam Alice";
            unlockedMember.FriendAvatarPath = "steam-avatar.png";
            var lockedMember = Achievement("Steam", "steam-alice", 10, gameId, "Steam Game", "SteamB", Utc(2026, 1, 1), RarityTier.Rare);
            lockedMember.Unlocked = false;
            lockedMember.UnlockTimeUtc = null;
            lockedMember.FriendName = "Steam Alice";
            lockedMember.FriendAvatarPath = "steam-avatar.png";
            var nonMember = Achievement("Steam", "bob", 10, gameId, "Steam Game", "SteamA", Utc(2026, 1, 2), RarityTier.Common);
            nonMember.FriendName = "Bob";
            // Fresh SQL-load shape: achievements only, no Friends summaries, no FriendGroupId.
            var data = new FriendsOverviewData
            {
                AllAchievements = new List<FriendAchievementDisplayItem> { unlockedMember, lockedMember, nonMember }
            };

            var rows = FriendOverviewProjection.ApplyMergeIdentity(data, settings);

            Assert.AreSame(unlockedMember, rows[0]);
            var mergedKey = FriendOverviewProjection.BuildFriendKey(FriendOverviewProjection.MergedProviderKey, group.Id);
            Assert.AreEqual(group.Id, unlockedMember.FriendGroupId);
            Assert.AreEqual(group.Id, lockedMember.FriendGroupId);
            Assert.AreEqual(mergedKey, unlockedMember.FriendScopeKey);
            Assert.AreEqual(mergedKey, lockedMember.FriendScopeKey);
            Assert.AreEqual("Alice Unified", unlockedMember.FriendName);
            Assert.AreEqual("steam-avatar.png", unlockedMember.FriendAvatarPath);
            Assert.IsNull(nonMember.FriendGroupId);
            Assert.AreEqual("Bob", nonMember.FriendName);
        }

        [TestMethod]
        public void ApplyMergeIdentity_WithoutMergeGroupsAppliesIndividualNicknames()
        {
            var settings = new PersistedSettings();
            settings.AddOrUpdateFriend(
                "Steam",
                "steam-alice",
                "Steam Alice",
                null,
                null,
                FriendSettingsSource.AutoDiscovered);
            settings.SetFriendNickname("Steam", "steam-alice", "Ally");
            var gameId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            var row = Achievement("Steam", "steam-alice", 10, gameId, "Steam Game", "SteamA", Utc(2026, 1, 1), RarityTier.Common);
            row.FriendName = "Steam Alice";
            var data = new FriendsOverviewData
            {
                AllAchievements = new List<FriendAchievementDisplayItem> { row }
            };

            var rows = FriendOverviewProjection.ApplyMergeIdentity(data, settings);

            Assert.AreSame(row, rows.Single());
            Assert.IsNull(row.FriendGroupId);
            Assert.AreEqual("Ally", row.FriendName);
        }

        [TestMethod]
        public void ApplyMergeIdentity_FormatsProviderNicknameUnderDefaultMode()
        {
            var settings = new PersistedSettings();
            settings.AddOrUpdateFriend(new FriendIdentity
            {
                ProviderKey = "Steam",
                ExternalUserId = "steam-alice",
                DisplayName = "Steam Alice",
                ProviderNickname = "Ally"
            });
            var gameId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var row = Achievement("Steam", "steam-alice", 10, gameId, "Steam Game", "SteamA", Utc(2026, 1, 1), RarityTier.Common);
            row.FriendName = "Steam Alice";
            var data = new FriendsOverviewData
            {
                AllAchievements = new List<FriendAchievementDisplayItem> { row }
            };

            FriendOverviewProjection.ApplyMergeIdentity(data, settings);

            Assert.AreEqual("Steam Alice (Ally)", row.FriendName);
        }

        [TestMethod]
        public void ApplyMergeIdentity_HonorsNameDisplayModeAndManualNicknamePrecedence()
        {
            var settings = new PersistedSettings { FriendNameDisplayMode = FriendNameDisplayMode.Nickname };
            settings.AddOrUpdateFriend(new FriendIdentity
            {
                ProviderKey = "Steam",
                ExternalUserId = "steam-alice",
                DisplayName = "Steam Alice",
                ProviderNickname = "Ally"
            });
            var gameId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            var row = Achievement("Steam", "steam-alice", 10, gameId, "Steam Game", "SteamA", Utc(2026, 1, 1), RarityTier.Common);
            row.FriendName = "Steam Alice";
            var data = new FriendsOverviewData
            {
                AllAchievements = new List<FriendAchievementDisplayItem> { row }
            };

            FriendOverviewProjection.ApplyMergeIdentity(data, settings);
            Assert.AreEqual("Ally", row.FriendName);

            // A manual plugin rename beats the mode formatting.
            settings.SetFriendNickname("Steam", "steam-alice", "Bestie");
            row.FriendName = "Steam Alice";
            FriendOverviewProjection.ApplyMergeIdentity(data, settings);
            Assert.AreEqual("Bestie", row.FriendName);
        }

        [TestMethod]
        public void Projection_FormatsFriendSummaryDisplayNameWithProviderNickname()
        {
            var settings = new PersistedSettings();
            settings.AddOrUpdateFriend(new FriendIdentity
            {
                ProviderKey = "Steam",
                ExternalUserId = "steam-alice",
                DisplayName = "Steam Alice",
                ProviderNickname = "Ally"
            });
            settings.AddOrUpdateFriend(new FriendIdentity
            {
                ProviderKey = "RetroAchievements",
                ExternalUserId = "retro-bob",
                DisplayName = "Retro Bob"
            });
            var data = new FriendsOverviewData
            {
                Friends = new List<FriendSummaryItem>
                {
                    new FriendSummaryItem { ProviderKey = "Steam", ExternalUserId = "steam-alice", DisplayName = "Steam Alice" },
                    new FriendSummaryItem { ProviderKey = "RetroAchievements", ExternalUserId = "retro-bob", DisplayName = "Retro Bob" }
                }
            };

            var projection = new FriendOverviewProjection(data, settings);

            Assert.AreEqual(
                "Steam Alice (Ally)",
                projection.Friends.Single(friend => friend.ExternalUserId == "steam-alice").DisplayName);
            // Providers without nickname data keep the plain display name in every mode.
            Assert.AreEqual(
                "Retro Bob",
                projection.Friends.Single(friend => friend.ExternalUserId == "retro-bob").DisplayName);
        }

        [TestMethod]
        public void ScopeKeys_AreStableAndCaseInsensitive()
        {
            var gameId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            Assert.AreEqual("steam|alice", FriendOverviewProjection.BuildFriendKey(" Steam ", " Alice "));
            Assert.AreEqual("steam|app:10", FriendOverviewProjection.BuildGameUnlockKey(" Steam ", null, 10, gameId));
            Assert.AreEqual("exophase|key:ps5|foo", FriendOverviewProjection.BuildGameUnlockKey(" Exophase ", " PS5|foo ", 0, null));
            Assert.AreEqual(
                "alice|steam|app:10",
                FriendOverviewProjection.BuildFriendGameUnlockKey(" Steam ", " Alice ", null, 10, gameId));
        }

        private static FriendAchievementDisplayItem Achievement(
            string providerKey,
            string externalUserId,
            int appId,
            Guid playniteGameId,
            string gameName,
            string apiName,
            DateTime unlockTimeUtc,
            RarityTier rarity)
        {
            return new FriendAchievementDisplayItem
            {
                ProviderKey = providerKey,
                FriendExternalUserId = externalUserId,
                AppId = appId,
                PlayniteGameId = playniteGameId,
                GameName = gameName,
                SortingName = gameName,
                ApiName = apiName,
                DisplayName = apiName,
                Unlocked = true,
                UnlockTimeUtc = unlockTimeUtc,
                Rarity = rarity
            };
        }

        private static DateTime Utc(int year, int month, int day)
        {
            return DateTime.SpecifyKind(new DateTime(year, month, day, 0, 0, 0), DateTimeKind.Utc);
        }

        private static PersistedSettings CreateMergeSettings(string groupNickname, string avatarProviderKey)
        {
            var settings = new PersistedSettings();
            settings.AddOrUpdateFriend(
                "Steam",
                "steam-alice",
                "Steam Alice",
                null,
                null,
                FriendSettingsSource.AutoDiscovered);
            settings.AddOrUpdateFriend(
                "Exophase",
                "exo-alice",
                "Exo Alice",
                null,
                null,
                FriendSettingsSource.Manual);

            var avatar = string.Equals(avatarProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase)
                ? FriendAccountRef.From("Exophase", "exo-alice")
                : null;
            settings.AddOrUpdateFriendMergeGroup(
                new[]
                {
                    FriendAccountRef.From("Steam", "steam-alice"),
                    FriendAccountRef.From("Exophase", "exo-alice")
                },
                groupNickname,
                avatar);
            return settings;
        }
    }
}
