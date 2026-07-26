using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RefreshRuntimeFriendTests
    {
        [TestMethod]
        public async Task RefreshAsync_Full_FetchesOwnershipForAllFriendsAndRefreshesCandidates()
        {
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate>
                {
                    MakeCandidate("1", 100),
                    MakeCandidate("2", 200)
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2")
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Full },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.BeginCalls);
            Assert.AreEqual(1, friends.EndCalls);
            // Full now fetches ownership for every friend (no per-friend opt-in) and builds mapped
            // scrape candidates from the fresh ownership snapshot rather than the cache loader.
            Assert.AreEqual(2, friends.GetOwnedGamesCalls);
            Assert.AreEqual(2, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(2, payload.FriendSummary.FriendsFetched);
            Assert.AreEqual(2, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task ExecuteRefreshAsync_UnifiedOptions_InterleavesCurrentAndFriendWorkByProvider()
        {
            var events = new List<string>();
            var sync = new object();
            void Record(string value)
            {
                lock (sync)
                {
                    events.Add(value);
                }
            }

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Steam Game" };
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OperationLog = Record
            };
            var provider = new FakeDataProvider("Steam", friends)
            {
                OperationLog = Record
            };
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.IncludeUnplayedGames = true;
            settings.Persisted.EnableParallelProviderRefresh = false;
            settings.Persisted.ScanDelayMs = 0;
            settings.Persisted.MaxRetryAttempts = 0;

            var runtime = new RefreshRuntime(
                cache,
                settings,
                new FakePlayniteApi(new[] { game }),
                new IDataProvider[] { provider },
                new[] { "Steam" });

            await runtime.ExecuteRefreshAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Custom,
                Options = new RefreshOptions
                {
                    Subjects = RefreshSubjects.All,
                    ProviderKeys = new[] { "Steam" },
                    Scope = RefreshGameScope.Explicit,
                    PlayniteGameIds = new[] { gameId },
                    FriendExternalUserIds = new[] { "1" },
                    RunProvidersInParallelOverride = false
                }
            }).ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new[] { "Steam:current", "Steam:friend-begin" },
                events.Take(2).ToArray());
            Assert.AreEqual(1, provider.RefreshCalls);
            Assert.AreEqual(1, friends.BeginCalls);
            Assert.AreEqual(1, friends.EndCalls);
        }

        [TestMethod]
        public async Task ExecuteRefreshAsync_FullCurrentUser_SkipsCachedNoAchievementGames()
        {
            var noAchievementGameId = Guid.NewGuid();
            var achievementGameId = Guid.NewGuid();
            var noAchievementGame = new Game { Id = noAchievementGameId, Name = "No Achievements" };
            var achievementGame = new Game { Id = achievementGameId, Name = "Has Achievements" };
            var cache = new FakeFriendCache
            {
                CachedGameData = new Dictionary<string, GameAchievementData>(StringComparer.OrdinalIgnoreCase)
                {
                    [noAchievementGameId.ToString("D")] = new GameAchievementData
                    {
                        PlayniteGameId = noAchievementGameId,
                        ProviderKey = "Steam",
                        HasAchievements = false
                    }
                }
            };
            var provider = new FakeDataProvider("Steam", friends: null);
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.IncludeUnplayedGames = true;
            settings.Persisted.EnableParallelProviderRefresh = false;
            settings.Persisted.ScanDelayMs = 0;
            settings.Persisted.MaxRetryAttempts = 0;

            var runtime = new RefreshRuntime(
                cache,
                settings,
                new FakePlayniteApi(new[] { noAchievementGame, achievementGame }),
                new IDataProvider[] { provider },
                new[] { "Steam" });

            await runtime.ExecuteRefreshAsync(new RefreshRequest
            {
                Mode = RefreshModeType.Full
            }).ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new[] { achievementGameId },
                provider.RefreshedGameIds.ToArray());
        }

        [TestMethod]
        public async Task RefreshAsync_Shared_RefreshesOwnershipBeforeAchievements()
        {
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate>
                {
                    MakeCandidate("1", 100)
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2")
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(2, friends.GetOwnedGamesCalls);
            Assert.AreEqual(2, cache.SaveFriendOwnershipCalls);
            Assert.IsTrue(cache.SavedOwnershipOptions.All(options => options.PruneStaleShared));
            // Shared builds candidates from the fresh ownership snapshot: both friends share the game,
            // so each gets a per-friend unlock scrape.
            Assert.AreEqual(2, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task RefreshAsync_EmptyOwnershipResult_DoesNotPruneSharedOwnership()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = Array.Empty<FriendGameOwnership>()
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, cache.SaveFriendOwnershipCalls);
            Assert.IsFalse(cache.SavedOwnershipOptions.Single().PruneStaleShared);
        }

        [TestMethod]
        public async Task RefreshAsync_FriendSelection_OnlyRefreshesSelectedFriendOwnership()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2"),
                    MakeFriend("3")
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2", "3");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Shared,
                        FriendExternalUserIds = new[] { "2" }
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            // Only the selected friend's library is fetched, even though three friends exist.
            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, cache.SaveFriendOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FriendAccountSelection_DoesNotMatchSameExternalIdOnOtherProviders()
        {
            var cache = new FakeFriendCache
            {
                CachedFriends = new List<FriendIdentity>
                {
                    MakeFriend("same")
                }
            };
            var steamFriends = new FakeFriendsProvider("Steam");
            var exophaseFriends = new FakeFriendsProvider("Exophase");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[]
                    {
                        new FakeDataProvider("Steam", steamFriends),
                        new FakeDataProvider("Exophase", exophaseFriends)
                    },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Shared,
                        FriendAccounts = new[] { FriendAccountRef.From("Steam", "same") },
                        FriendExternalUserIds = new[] { "same" }
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, steamFriends.GetOwnedGamesCalls);
            Assert.AreEqual(0, exophaseFriends.GetOwnedGamesCalls);
            Assert.AreEqual(1, cache.SaveFriendOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_Full_UsesConfiguredFriendsWithoutRosterMetadataFetchOrSave()
        {
            var cache = new FakeFriendCache();
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.AddOrUpdateFriend(
                "Steam",
                "1",
                "Configured Friend",
                "https://example.invalid/configured-avatar.png",
                "icon_cache/friendavatars/steam_1.png",
                FriendSettingsSource.Manual);
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    new FriendIdentity
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        DisplayName = "Provider Friend",
                        AvatarUrl = "https://example.invalid/provider-avatar.png"
                    }
                }
            };

            await new RefreshRuntime(cache, settings)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Full },
                    reportProgress: null)
                .ConfigureAwait(false);

            var configured = settings.Persisted.GetFriendSetting("Steam", "1");
            Assert.AreEqual(0, friends.GetFriendsCalls);
            Assert.AreEqual(0, cache.SaveFriendListCalls);
            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual("Configured Friend", configured.DisplayName);
            Assert.AreEqual("https://example.invalid/configured-avatar.png", configured.AvatarUrl);
            Assert.AreEqual("icon_cache/friendavatars/steam_1.png", configured.AvatarPath);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_UsesCachedFriendsWithoutRosterMetadataFetchOrSave()
        {
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate> { MakeCandidate("1", 100) }
            };
            SeedCachedFriends(cache, "Steam", "1");
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    new FriendIdentity
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        DisplayName = "Provider Friend",
                        AvatarUrl = "https://example.invalid/provider-avatar.png"
                    }
                }
            };

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetFriendsCalls);
            Assert.AreEqual(0, cache.SaveFriendListCalls);
            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_Full_NoConfiguredOrCachedFriendsSkipsProviderRosterDiscovery()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Full },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.BeginCalls);
            Assert.AreEqual(1, friends.EndCalls);
            Assert.AreEqual(0, friends.GetFriendsCalls);
            Assert.AreEqual(0, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, cache.SaveFriendListCalls);
            Assert.AreEqual(0, payload.FriendSummary.FriendsFetched);
        }

        [TestMethod]
        public async Task RefreshFriendRosterAsync_FetchesProviderMetadataAndSavesFriendList()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    new FriendIdentity
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        DisplayName = "Provider Friend",
                        AvatarUrl = "https://example.invalid/provider-avatar.png"
                    }
                }
            };

            var saved = await CreateRuntime(cache)
                .RefreshFriendRosterAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) })
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.BeginCalls);
            Assert.AreEqual(1, friends.GetFriendsCalls);
            Assert.AreEqual(1, friends.EndCalls);
            Assert.AreEqual(1, cache.SaveFriendListCalls);
            Assert.AreEqual(1, saved);
        }

        [TestMethod]
        public async Task RefreshFriendRosterAsync_WithProviderKeys_PassesScopedAuthContext()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            var provider = new FakeDataProvider("Steam", friends);
            var settings = new PlayniteAchievementsSettings();
            var runtime = new RefreshRuntime(
                cache,
                settings,
                new FakePlayniteApi(Array.Empty<Game>()),
                new IDataProvider[] { provider },
                new[] { "Steam" });

            var saved = await runtime
                .RefreshFriendRosterAsync(new[] { "Steam" }, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(1, provider.BeginAuthContextCalls);
            Assert.AreEqual(1, provider.EndAuthContextCalls);
            Assert.AreEqual(1, friends.BeginCalls);
            Assert.AreEqual(1, friends.GetFriendsCalls);
            Assert.AreEqual(1, friends.EndCalls);
            Assert.AreEqual(1, saved);
        }

        [TestMethod]
        public async Task RefreshAsync_SelectedGame_UsesCachedCandidateWithoutOwnershipRefresh()
        {
            var gameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate>
                {
                    new FriendRefreshCandidate
                    {
                        Friend = MakeFriend("2"),
                        AppId = 200,
                        PlayniteGameId = gameId,
                        GameName = "Game 200"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2")
                }
            };

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.SelectedGame,
                        PlayniteGameIds = new[] { gameId },
                        FriendExternalUserIds = new[] { "2" },
                        ForceDefinitionRefresh = true
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, friends.GetFriendsCalls);
            Assert.AreEqual(0, cache.SaveFriendListCalls);
            Assert.AreEqual(0, cache.SaveFriendOwnershipCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(FriendRefreshScope.SelectedGame, cache.LastLoadOptions.Scope);
            CollectionAssert.AreEqual(new[] { gameId }, cache.LastLoadOptions.PlayniteGameIds.ToList());
        }

        [TestMethod]
        public async Task RefreshAsync_SelectedGameForAllFriends_DoesNotLoadRosterOrOwnership()
        {
            var gameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate>
                {
                    new FriendRefreshCandidate
                    {
                        Friend = MakeFriend("1"),
                        AppId = 200,
                        PlayniteGameId = gameId,
                        GameName = "Game 200"
                    },
                    new FriendRefreshCandidate
                    {
                        Friend = MakeFriend("2"),
                        AppId = 200,
                        PlayniteGameId = gameId,
                        GameName = "Game 200"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2"),
                    MakeFriend("3")
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2", "3");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.SelectedGame,
                        PlayniteGameIds = new[] { gameId },
                        ForceDefinitionRefresh = true
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetFriendsCalls);
            Assert.AreEqual(0, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, cache.SaveFriendListCalls);
            Assert.AreEqual(0, cache.SaveFriendOwnershipCalls);
            Assert.AreEqual(2, friends.GetFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_RefreshesAllLoadedCandidates()
        {
            var cache = new FakeFriendCache
            {
                Candidates = Enumerable.Range(1, 65)
                    .Select(index => MakeCandidate("1", 1000 + index))
                    .ToList()
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(65, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(65, payload.FriendSummary.CandidatesLoaded);
            Assert.AreEqual(65, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_PromotesCachedProviderOnlyRowsFromCurrentUserLabels()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate>
                {
                    MakeCandidate("1", 100)
                },
                CurrentUserGameLabels = new List<CurrentUserGameLabel>
                {
                    new CurrentUserGameLabel
                    {
                        ProviderKey = "Steam",
                        PlayniteGameId = playniteGameId,
                        AppId = 100,
                        GameName = "Game 100"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, cache.PromoteProviderOnlyGameCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryFriend_DiscoversDefinitionsAndPersistsProviderOnlyOwnership()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 1
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, cache.SaveFriendGameDefinitionCalls);
            Assert.AreEqual(1, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryFriend_SkipsProviderOnlyOwnershipForNoAchievementsDefinition()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 1
                    }
                },
                DefinitionStatusToReturn = FriendGameDefinitionStatus.NoAchievements
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, cache.SaveFriendGameDefinitionCalls);
            Assert.AreEqual(0, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryProviderOnlyGameWithZeroUnlocks_DoesNotPersistFriendData()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 0
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>()
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(0, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(0, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryProviderOnlyGameWithFriendUnlocks_PersistsOwnershipAndAchievements()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 1
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryProviderOnlyGameAlreadyProbed_SkipsCandidateRescrape()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false,
                Candidates = new List<FriendRefreshCandidate>
                {
                    new FriendRefreshCandidate
                    {
                        Friend = MakeFriend("1"),
                        AppId = 100,
                        GameName = "Game 100"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 1
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryProviderOnlyGameWithUnknownUnlockHint_ScrapesAndPersists()
        {
            // A provider that supplies no unlock hint (e.g. Steam via community page parse failure)
            // must still have its provider-only games scraped under Full scope rather than silently
            // dropped. The flow is probe-first: the friend achievements page is scraped, and because
            // it confirms unlocks the game definition is then fetched and everything persists.
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1
                        // AchievementUnlocksHint intentionally unset (null).
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            // The unknown-hint provider-only game is scraped (not silently dropped) and, because it
            // has unlocks, its achievements are persisted. Provider-only ownership save counts are
            // intentionally not asserted here (a separate redundancy in the ownership-save path).
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);

            // Probe-first: the definition is only fetched after the probe confirms unlocks.
            var order = friends.NetworkCallOrder.ToList();
            Assert.IsTrue(
                order.IndexOf("achievements:1") < order.IndexOf("definition:100"),
                $"Expected the achievements probe before the definition fetch but got: {string.Join(", ", order)}");
        }

        [TestMethod]
        public async Task RefreshAsync_FullProviderOnlyGameWithUnknownHintAndZeroUnlocks_LeavesNoTrace()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1
                        // AchievementUnlocksHint intentionally unset (null).
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>()
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            // One probe is unavoidable to learn the unlock count; with zero unlocks nothing else is
            // fetched and nothing is persisted (no definition rows, no ownership, no achievements).
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(0, cache.SaveFriendGameDefinitionCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(0, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullSharedUnknownHintGame_FetchesDefinitionOnceForConfirmedFriendOnly()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1"), MakeFriend("2") },
                // The fake returns the same owned-games list for every friend, so this models one
                // unknown-hint game both friends own.
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1
                    }
                },
                AchievementRowsByFriend = new Dictionary<string, List<FriendAchievementRow>>
                {
                    ["1"] = new List<FriendAchievementRow>(),
                    ["2"] = new List<FriendAchievementRow>
                    {
                        new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                    }
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            // Both owners are probed; the shared definition is fetched exactly once (triggered by the
            // friend with unlocks) and only that friend's ownership/achievements persist.
            Assert.AreEqual(2, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, cache.SaveFriendGameDefinitionCalls);
            Assert.AreEqual(1, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullMixedHintOwnersOfOneGame_FetchDefinitionEagerly()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1"), MakeFriend("2") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 1
                    },
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "2",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1
                        // AchievementUnlocksHint intentionally unset (null).
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            // Any owner with a positive unlock hint keeps the game's definition fetch eager: the
            // definition is fetched once, before any probe.
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            var order = friends.NetworkCallOrder.ToList();
            var definitionIndex = order.IndexOf("definition:100");
            var firstProbeIndex = order.FindIndex(entry => entry.StartsWith("achievements:", StringComparison.Ordinal));
            Assert.IsTrue(
                definitionIndex >= 0 && firstProbeIndex >= 0 && definitionIndex < firstProbeIndex,
                $"Expected the definition fetch before any probe but got: {string.Join(", ", order)}");
        }

        [TestMethod]
        public async Task RefreshAsync_SharedSteamGameMatchingCurrentUserLabel_SavesOwnershipEvenWithZeroUnlocks()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false,
                CurrentUserGameLabels = new List<CurrentUserGameLabel>
                {
                    new CurrentUserGameLabel
                    {
                        ProviderKey = "Steam",
                        PlayniteGameId = playniteGameId,
                        AppId = 100,
                        GameName = "Game 100"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 0
                    }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Shared
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            // The current-user label stamps the Playnite mapping onto the fetched ownership item, so
            // the shared save persists the game even though the friend has zero unlocks — this is what
            // makes shared library games appear in the friends overview.
            var saved = cache.SavedOwnershipRows.Single(row => row.AppId == 100);
            Assert.AreEqual(playniteGameId, saved.PlayniteGameId);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_SharedRetroAchievementsGameMatchingCurrentUserLabel_StampsPlayniteMapping()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false,
                CurrentUserGameLabels = new List<CurrentUserGameLabel>
                {
                    new CurrentUserGameLabel
                    {
                        ProviderKey = "RetroAchievements",
                        PlayniteGameId = playniteGameId,
                        AppId = 4111,
                        GameName = "RA Game"
                    }
                }
            };
            var friends = new FakeFriendsProvider("RetroAchievements")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1", "RetroAchievements") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "RetroAchievements",
                        ExternalUserId = "1",
                        AppId = 4111,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 0
                    }
                }
            };
            SeedCachedFriends(cache, "RetroAchievements", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("RetroAchievements", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Shared
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            var saved = cache.SavedOwnershipRows.Single(row => row.AppId == 4111);
            Assert.AreEqual(playniteGameId, saved.PlayniteGameId);
        }

        [TestMethod]
        public async Task RefreshAsync_ExophaseOwnershipNeverStampedFromCurrentUserLabels()
        {
            // Exophase resolves inline Playnite ids itself with platform-aware name matching; naive
            // (AppId/key) stamping must not apply to it.
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false,
                CurrentUserGameLabels = new List<CurrentUserGameLabel>
                {
                    new CurrentUserGameLabel
                    {
                        ProviderKey = "Exophase",
                        PlayniteGameId = Guid.NewGuid(),
                        ProviderGameKey = "exo-game",
                        GameName = "Exo Game"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Exophase")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1", "Exophase") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "1",
                        ProviderGameKey = "exo-game",
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 0
                    }
                }
            };
            SeedCachedFriends(cache, "Exophase", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Exophase", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Shared
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.IsFalse(cache.SavedOwnershipRows.Any(row => row.PlayniteGameId.HasValue));
        }

        [TestMethod]
        public async Task RefreshAsync_RetroAchievementsFullLibraryProviderOnlyGameWithZeroUnlocks_DoesNotPersistFriendData()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("RetroAchievements")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1", "RetroAchievements") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "RetroAchievements",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 0
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>()
            };
            SeedCachedFriends(cache, "RetroAchievements", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("RetroAchievements", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(0, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(0, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_RetroAchievementsFullLibraryProviderOnlyGameWithFriendUnlocks_PersistsOwnershipAndAchievements()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("RetroAchievements")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1", "RetroAchievements") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "RetroAchievements",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 1,
                        AchievementUnlocksHint = 1
                    }
                },
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow { ApiName = "A", DisplayName = "A", Unlocked = true }
                }
            };
            SeedCachedFriends(cache, "RetroAchievements", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("RetroAchievements", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_RetroAchievementsSharedOwnershipKeepsNumericAppIdAndEmptyProviderKey()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("RetroAchievements")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1", "RetroAchievements") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "RetroAchievements",
                        ExternalUserId = "1",
                        AppId = 12345,
                        ProviderGameKey = null,
                        GameName = "RA Game"
                    }
                }
            };
            SeedCachedFriends(cache, "RetroAchievements", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("RetroAchievements", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, cache.SaveFriendOwnershipCalls);

            var saved = cache.SavedOwnershipRows.Single();
            Assert.AreEqual(12345, saved.AppId);
            Assert.IsTrue(string.IsNullOrWhiteSpace(saved.ProviderGameKey));
        }

        [TestMethod]
        public async Task RefreshAsync_SteamFriendOwnership_AugmentsSteamRowsWithMergedExophaseSteamRows()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                FriendGameMappings = new List<FriendGameMapping>
                {
                    new FriendGameMapping
                    {
                        AppId = 480,
                        PlayniteGameId = playniteGameId
                    }
                }
            };
            var steamFriends = new FakeFriendsProvider("Steam")
            {
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "steam-1",
                        AppId = 999,
                        GameName = "Steam Base"
                    }
                }
            };
            var exophaseFriends = new FakeFriendsProvider("Exophase")
            {
                SteamOwnedGamesSupplementToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-1",
                        AppId = 999,
                        ProviderGameKey = "steam|steam-base",
                        ProviderPlatformKey = "Steam",
                        GameName = "Steam Base Duplicate",
                        AchievementUnlocksHint = 1,
                        AchievementTotalHint = 5
                    },
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-1",
                        AppId = 480,
                        ProviderGameKey = "steam|shared-game",
                        ProviderPlatformKey = "Steam",
                        GameName = "Shared Game",
                        PlaytimeForeverMinutes = 42,
                        AchievementUnlocksHint = 2,
                        AchievementTotalHint = 5
                    }
                }
            };
            var steamProvider = new FakeDataProvider("Steam", steamFriends);
            var exophaseProvider = new FakeDataProvider("Exophase", exophaseFriends);
            var allProviders = new IDataProvider[] { steamProvider, exophaseProvider };

            await CreateRuntime(
                    cache,
                    configureSettings: settings => ConfigureExophaseSteamFriendOwnership(settings, "steam-1", "exo-1"),
                    providers: allProviders)
                .RefreshAsync(
                    new IDataProvider[] { steamProvider },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, steamFriends.GetOwnedGamesCalls);
            Assert.AreEqual(1, exophaseFriends.GetSteamOwnedGamesCalls);
            Assert.AreEqual(999, exophaseFriends.LastKnownSteamOwnership.Single().AppId);

            Assert.AreEqual(2, cache.SavedOwnershipRows.Count);
            Assert.AreEqual(1, cache.SavedOwnershipRows.Count(row => row.AppId == 999));
            var saved = cache.SavedOwnershipRows.Single(row => row.AppId == 480);
            Assert.AreEqual("Steam", saved.ProviderKey);
            Assert.AreEqual("steam-1", saved.ExternalUserId);
            Assert.AreEqual(480, saved.AppId);
            Assert.AreEqual(playniteGameId, saved.PlayniteGameId);
            Assert.AreEqual("Shared Game", saved.GameName);
            Assert.AreEqual(42, saved.PlaytimeForeverMinutes);
            Assert.AreEqual(2, saved.AchievementUnlocksHint);
            Assert.AreEqual(5, saved.AchievementTotalHint);
        }

        [TestMethod]
        public async Task RefreshAsync_SteamFriendOwnership_UsesPlayniteSteamAppIdWhenCacheMappingMissing()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache();
            var steamFriends = new FakeFriendsProvider("Steam")
            {
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership { ProviderKey = "Steam", ExternalUserId = "steam-1", AppId = 999 }
                }
            };
            var exophaseFriends = new FakeFriendsProvider("Exophase")
            {
                SteamOwnedGamesSupplementToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-1",
                        AppId = 12345,
                        ProviderGameKey = "steam|family-shared-game",
                        ProviderPlatformKey = "Steam",
                        GameName = "Family Shared Game",
                        PlaytimeForeverMinutes = 64
                    }
                }
            };
            var steamProvider = new FakeDataProvider("Steam", steamFriends);
            var exophaseProvider = new FakeDataProvider("Exophase", exophaseFriends);
            var allProviders = new IDataProvider[] { steamProvider, exophaseProvider };
            var playniteSteamGame = new Game
            {
                Id = playniteGameId,
                Name = "Family Shared Game",
                GameId = "12345",
                PluginId = SteamGameIdentity.SteamPluginId
            };

            await CreateRuntime(
                    cache,
                    configureSettings: settings => ConfigureExophaseSteamFriendOwnership(settings, "steam-1", "exo-1"),
                    providers: allProviders,
                    playniteGames: new[] { playniteSteamGame })
                .RefreshAsync(
                    new IDataProvider[] { steamProvider },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, steamFriends.GetOwnedGamesCalls);
            Assert.AreEqual(1, exophaseFriends.GetSteamOwnedGamesCalls);
            Assert.AreEqual(999, exophaseFriends.LastKnownSteamOwnership.Single().AppId);

            Assert.AreEqual(2, cache.SavedOwnershipRows.Count);
            var saved = cache.SavedOwnershipRows.Single(row => row.AppId == 12345);
            Assert.AreEqual("Steam", saved.ProviderKey);
            Assert.AreEqual("steam-1", saved.ExternalUserId);
            Assert.AreEqual(12345, saved.AppId);
            Assert.AreEqual(playniteGameId, saved.PlayniteGameId);
            Assert.AreEqual("Family Shared Game", saved.GameName);
            Assert.AreEqual(64, saved.PlaytimeForeverMinutes);
        }

        [TestMethod]
        public async Task RefreshAsync_SteamFriendOwnership_DiscoversProviderOnlyExophaseSteamAppIds()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var steamFriends = new FakeFriendsProvider("Steam")
            {
                OwnedGamesToReturn = Array.Empty<FriendGameOwnership>(),
                AchievementRowsToReturn = new List<FriendAchievementRow>
                {
                    new FriendAchievementRow
                    {
                        ApiName = "ACH_WIN",
                        DisplayName = "Win",
                        Unlocked = true,
                        UnlockTimeUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)
                    }
                }
            };
            var exophaseFriends = new FakeFriendsProvider("Exophase")
            {
                SteamOwnedGamesSupplementToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "exo-1",
                        AppId = 3768760,
                        ProviderGameKey = "steam|provider-only-game",
                        ProviderPlatformKey = "Steam",
                        GameName = "Provider Only Game",
                        PlaytimeForeverMinutes = 12,
                        AchievementUnlocksHint = 1,
                        AchievementTotalHint = 10
                    }
                }
            };
            var steamProvider = new FakeDataProvider("Steam", steamFriends);
            var exophaseProvider = new FakeDataProvider("Exophase", exophaseFriends);
            var allProviders = new IDataProvider[] { steamProvider, exophaseProvider };

            await CreateRuntime(
                    cache,
                    configureSettings: settings => ConfigureExophaseSteamFriendOwnership(settings, "steam-1", "exo-1"),
                    providers: allProviders)
                .RefreshAsync(
                    new IDataProvider[] { steamProvider },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Full },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, steamFriends.GetOwnedGamesCalls);
            Assert.AreEqual(1, exophaseFriends.GetSteamOwnedGamesCalls);
            Assert.AreEqual(1, steamFriends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, steamFriends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, cache.SaveProviderOnlyOwnershipCalls);
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);

            var saved = cache.SavedOwnershipRows.Last();
            Assert.AreEqual("Steam", saved.ProviderKey);
            Assert.AreEqual("steam-1", saved.ExternalUserId);
            Assert.AreEqual(3768760, saved.AppId);
            Assert.IsTrue(string.IsNullOrWhiteSpace(saved.ProviderGameKey));
            Assert.IsNull(saved.PlayniteGameId);
            Assert.AreEqual("Provider Only Game", saved.GameName);
        }

        [TestMethod]
        public async Task RefreshAsync_SteamFriendOwnership_FallsBackToSteamWhenExophaseFails()
        {
            var cache = new FakeFriendCache();
            var steamFriends = new FakeFriendsProvider("Steam")
            {
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "steam-1",
                        AppId = 999,
                        GameName = "Steam Fallback"
                    }
                }
            };
            var exophaseFriends = new FakeFriendsProvider("Exophase")
            {
                SteamOwnedGamesSupplementResult =
                    FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.Failed("scrape failed")
            };
            var steamProvider = new FakeDataProvider("Steam", steamFriends);
            var exophaseProvider = new FakeDataProvider("Exophase", exophaseFriends);
            var allProviders = new IDataProvider[] { steamProvider, exophaseProvider };

            await CreateRuntime(
                    cache,
                    configureSettings: settings => ConfigureExophaseSteamFriendOwnership(settings, "steam-1", "exo-1"),
                    providers: allProviders)
                .RefreshAsync(
                    new IDataProvider[] { steamProvider },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, exophaseFriends.GetSteamOwnedGamesCalls);
            Assert.AreEqual(1, steamFriends.GetOwnedGamesCalls);

            var saved = cache.SavedOwnershipRows.Single();
            Assert.AreEqual("Steam", saved.ProviderKey);
            Assert.AreEqual("steam-1", saved.ExternalUserId);
            Assert.AreEqual(999, saved.AppId);
            Assert.AreEqual("Steam Fallback", saved.GameName);
        }

        [TestMethod]
        public async Task RefreshAsync_ProviderOnlyCandidateWithZeroUnlockHint_SkipsDetailFetch()
        {
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false,
                Candidates = new List<FriendRefreshCandidate>
                {
                    new FriendRefreshCandidate
                    {
                        Friend = MakeFriend("1"),
                        AppId = 100,
                        GameName = "Game 100"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                AchievementRowsToReturn = new List<FriendAchievementRow>()
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, cache.SaveFriendGameAchievementsCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryFriend_RetriesCachedNoAchievementsDefinition()
        {
            var cache = new FakeFriendCache
            {
                DefinitionStates = new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["100"] = new FriendGameDefinitionState
                    {
                        ProviderKey = "Steam",
                        AppId = 100,
                        Status = FriendGameDefinitionStatus.NoAchievements,
                        LastCheckedUtc = DateTime.UtcNow
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                DefinitionStatusToReturn = FriendGameDefinitionStatus.NoAchievements
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            // The mapped game's ownership is synced by the per-friend save, not re-flagged as
            // provider-only, so no provider-only ownership write occurs.
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_CustomProviderAppIds_DiscoversOnlyRequestedProviderApps()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership { ProviderKey = "Steam", ExternalUserId = "1", AppId = 100 },
                    new FriendGameOwnership { ProviderKey = "Steam", ExternalUserId = "1", AppId = 200 }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Custom,
                        ProviderAppIds = new[] { 200 }
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            CollectionAssert.AreEqual(new[] { 200 }, friends.DefinitionAppIds.ToList());
            Assert.AreEqual(1, cache.SavedOwnershipRows.Count);
            Assert.AreEqual(200, cache.SavedOwnershipRows.Single().AppId);
            Assert.IsFalse(cache.SavedOwnershipOptions.Single().PruneStaleShared);
            // The requested app is mapped, so its ownership is synced by the per-friend save; no
            // separate provider-only ownership write occurs.
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_SelectedGameWithExplicitProviderTarget_DoesNotPruneSharedOwnership()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership { ProviderKey = "Steam", ExternalUserId = "1", AppId = 100 },
                    new FriendGameOwnership { ProviderKey = "Steam", ExternalUserId = "1", AppId = 200 }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.SelectedGame,
                        ProviderAppIds = new[] { 200 }
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, cache.SaveFriendOwnershipCalls);
            Assert.AreEqual(1, cache.SavedOwnershipRows.Count);
            Assert.AreEqual(200, cache.SavedOwnershipRows.Single().AppId);
            Assert.IsFalse(cache.SavedOwnershipOptions.Single().PruneStaleShared);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_DoesNotDiscoverProviderOnlyOwnership()
        {
            // Recent is strictly playtime-delta based; it never discovers provider-only libraries.
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Recent
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_SharedScope_DoesNotDiscoverProviderOnlyDefinitions()
        {
            // The Shared scope never discovers unowned games.
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Shared
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_ExophaseFullScope_RefreshesSharedFriendOwnershipForMapping()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Exophase")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1"), MakeFriend("2") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "1",
                        ProviderGameKey = "origin|titanfall-2-origin",
                        ProviderPlatformKey = "EA",
                        PlayniteGameId = playniteGameId,
                        GameName = "Titanfall 2",
                        AchievementUnlocksHint = 1,
                        AchievementTotalHint = 50
                    }
                }
            };
            SeedCachedFriends(cache, "Exophase", "1", "2");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Exophase", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(2, friends.GetOwnedGamesCalls);
            Assert.AreEqual(2, cache.SaveFriendOwnershipCalls);
            // Exophase friend unlocks come from the earned-awards JSON endpoint whose rows carry
            // only stable award ids, so the mapped game's schema comes from a single definition
            // fetch shared across all friends.
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_ExophaseFullScope_ProbesUnknownHintRowsAndSkipsZeroTotalRows()
        {
            var playniteGameId = Guid.NewGuid();
            var cache = new FakeFriendCache
            {
                ProviderGamesMappedToPlayniteLibrary = false
            };
            var friends = new FakeFriendsProvider("Exophase")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1", "Exophase") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "1",
                        ProviderGameKey = "psn|mapped-no-signal",
                        ProviderPlatformKey = "PSN",
                        PlayniteGameId = playniteGameId,
                        GameName = "Mapped No Signal"
                    },
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "1",
                        ProviderGameKey = "psn|provider-no-signal",
                        ProviderPlatformKey = "PSN",
                        GameName = "Provider No Signal"
                    },
                    new FriendGameOwnership
                    {
                        ProviderKey = "Exophase",
                        ExternalUserId = "1",
                        ProviderGameKey = "psn|zero-total",
                        ProviderPlatformKey = "PSN",
                        GameName = "Zero Total",
                        AchievementUnlocksHint = 0,
                        AchievementTotalHint = 0
                    }
                }
            };
            SeedCachedFriends(cache, "Exophase", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Exophase", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Full },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            // The mapped game is refreshed regardless of hints: one eager schema (definition) fetch
            // and one unlock scrape.
            Assert.AreEqual(1, friends.GetFriendGameDefinitionCalls);
            Assert.AreEqual(1, cache.SaveFriendGameDefinitionCalls);
            // Two unlock scrapes: the mapped game plus the probe of the unknown-hint provider-only
            // row (probed rather than silently dropped). The zero-total row is never fetched.
            Assert.AreEqual(2, friends.GetFriendGameAchievementsCalls);
            // The empty probe leaves no trace, so only the mapped game's rows are saved.
            Assert.AreEqual(1, cache.SaveFriendGameAchievementsCalls);
            Assert.AreEqual(0, cache.SaveProviderOnlyOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_WithCachedOwnership_StillRefreshesFriendLibrary()
        {
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate> { MakeCandidate("1", 100) }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            // Recent must re-fetch ownership even for cached friends so LastOwnershipRefreshUtc
            // advances and the recency gate can detect new activity.
            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(1, cache.SaveFriendOwnershipCalls);
            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_RefreshesOwnershipForAllScopedFriends()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2")
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(2, friends.GetOwnedGamesCalls);
            Assert.AreEqual(2, cache.SaveFriendOwnershipCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_OwnershipFetchFails_SkipsFriendCandidates()
        {
            // Fail closed: without a fresh ownership snapshot, none of the friend's games can be
            // recency-confirmed, so their cached backlog must not be scraped blind.
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate> { MakeCandidate("1", 100) }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesResult = FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>
                    .Failed("profile temporarily unavailable")
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetOwnedGamesCalls);
            Assert.AreEqual(0, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_BoundsParallelAchievementRefresh()
        {
            var cache = new FakeFriendCache
            {
                Candidates = Enumerable.Range(1, 12)
                    .Select(index => MakeCandidate("1", 1000 + index))
                    .ToList()
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                AchievementDelayMs = 25
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(12, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(12, payload.FriendSummary.CandidatesRefreshed);
            Assert.IsTrue(friends.MaxConcurrentAchievementCalls > 1);
            Assert.IsTrue(friends.MaxConcurrentAchievementCalls <= 4);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_ParallelDisabled_RunsAchievementRefreshSequentially()
        {
            var cache = new FakeFriendCache
            {
                Candidates = Enumerable.Range(1, 8)
                    .Select(index => MakeCandidate("1", 1000 + index))
                    .ToList()
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                AchievementDelayMs = 10
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache, enableParallelProviderRefresh: false)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(8, payload.FriendSummary.CandidatesRefreshed);
            Assert.AreEqual(1, friends.MaxConcurrentAchievementCalls);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_SkipsSteamGameWithUnchangedPlaytime()
        {
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate> { MakeCandidate("1", 100) },
                OwnershipRecency = new Dictionary<string, FriendOwnershipRecency>(StringComparer.OrdinalIgnoreCase)
                {
                    ["100"] = new FriendOwnershipRecency
                    {
                        PlaytimeForeverMinutes = 50,
                        LastScrapedUtc = DateTime.UtcNow.AddDays(-1),
                        LastScrapeStatus = "ok"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 50
                    }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(0, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(0, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task RefreshAsync_Recent_ScrapesSteamGameWithIncreasedPlaytime()
        {
            var cache = new FakeFriendCache
            {
                Candidates = new List<FriendRefreshCandidate> { MakeCandidate("1", 100) },
                OwnershipRecency = new Dictionary<string, FriendOwnershipRecency>(StringComparer.OrdinalIgnoreCase)
                {
                    ["100"] = new FriendOwnershipRecency
                    {
                        PlaytimeForeverMinutes = 50,
                        LastScrapedUtc = DateTime.UtcNow.AddDays(-1),
                        LastScrapeStatus = "ok"
                    }
                }
            };
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") },
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership
                    {
                        ProviderKey = "Steam",
                        ExternalUserId = "1",
                        AppId = 100,
                        PlaytimeForeverMinutes = 75
                    }
                }
            };
            SeedCachedFriends(cache, "Steam", "1");

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Recent },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.GetFriendGameAchievementsCalls);
            Assert.AreEqual(1, payload.FriendSummary.CandidatesRefreshed);
        }

        [TestMethod]
        public async Task RefreshAsync_Shared_BoundsParallelOwnershipRefresh()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = Enumerable.Range(1, 12)
                    .Select(index => MakeFriend(index.ToString()))
                    .ToList(),
                OwnershipDelayMs = 25
            };
            SeedCachedFriends(cache, "Steam", Enumerable.Range(1, 12).Select(index => index.ToString()).ToArray());

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(12, friends.GetOwnedGamesCalls);
            Assert.AreEqual(12, payload.FriendSummary.OwnershipPagesRefreshed);
            Assert.IsTrue(friends.MaxConcurrentOwnershipCalls > 1);
            Assert.IsTrue(friends.MaxConcurrentOwnershipCalls <= 4);
        }

        [TestMethod]
        public async Task RefreshAsync_Shared_ReportsMonotonicProgressWithFriendAndGameNames()
        {
            var reports = new List<ProgressEvent>();
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity>
                {
                    MakeFriend("1"),
                    MakeFriend("2")
                },
                // Shared builds candidates from the fresh ownership snapshot; name the shared games so
                // the per-game progress detail reads "Friend 1 - Game 100".
                OwnedGamesToReturn = new List<FriendGameOwnership>
                {
                    new FriendGameOwnership { ProviderKey = "Steam", AppId = 100, GameName = "Game 100", PlaytimeForeverMinutes = 1 },
                    new FriendGameOwnership { ProviderKey = "Steam", AppId = 200, GameName = "Game 200", PlaytimeForeverMinutes = 1 }
                }
            };
            SeedCachedFriends(cache, "Steam", "1", "2");

            await CreateRuntime(cache, enableParallelProviderRefresh: false)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Shared },
                    reportProgress: (message, current, total) => reports.Add(new ProgressEvent(message, current, total)))
                .ConfigureAwait(false);

            Assert.IsTrue(reports.Count > 0);
            Assert.IsTrue(reports.All(report => report.Total == RefreshRuntime.FriendRefreshProgressSession.TotalUnits));
            for (var i = 1; i < reports.Count; i++)
            {
                Assert.IsTrue(
                    reports[i].Current >= reports[i - 1].Current,
                    $"Progress regressed from {reports[i - 1].Current} to {reports[i].Current}: {reports[i].Message}");
            }

            var messageText = string.Join("\n", reports.Select(report => report.Message ?? "<null>"));
            Assert.IsTrue(reports.Any(report => report.Message?.Contains("Friend 1") == true), messageText);
            Assert.IsTrue(reports.Any(report => report.Message?.Contains("Friend 1 - Game 100") == true), messageText);
        }

        [TestMethod]
        public async Task RefreshAsync_FullLibraryFriend_UsesUserFacingGameCheckProgressText()
        {
            var reports = new List<ProgressEvent>();
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                FriendsToReturn = new List<FriendIdentity> { MakeFriend("1") }
            };
            SeedCachedFriends(cache, "Steam", "1");

            await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions
                    {
                        Scope = FriendRefreshScope.Full
                    },
                    reportProgress: (message, current, total) => reports.Add(new ProgressEvent(message, current, total)))
                .ConfigureAwait(false);

            var messages = reports
                .Select(report => report.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList();
            Assert.IsTrue(
                messages.Any(message => message.IndexOf("Refreshing friend games", StringComparison.OrdinalIgnoreCase) >= 0),
                string.Join("\n", messages));
            Assert.IsFalse(messages.Any(message => message.IndexOf("definition", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.IsFalse(messages.Any(message => message.IndexOf("schema", StringComparison.OrdinalIgnoreCase) >= 0));
            Assert.IsFalse(messages.Any(message => message.IndexOf("unlock marker", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public async Task RefreshAsync_BeginAuthFailure_MarksAuthRequiredAndEndsRefresh()
        {
            var cache = new FakeFriendCache();
            var friends = new FakeFriendsProvider("Steam")
            {
                BeginResult = FriendsProviderResult<FriendsRefreshPreparation>.Failed("expired", authRequired: true)
            };

            var payload = await CreateRuntime(cache)
                .RefreshAsync(
                    new IDataProvider[] { new FakeDataProvider("Steam", friends) },
                    new FriendRefreshOptions { Scope = FriendRefreshScope.Full },
                    reportProgress: null)
                .ConfigureAwait(false);

            Assert.AreEqual(1, friends.BeginCalls);
            Assert.AreEqual(1, friends.EndCalls);
            Assert.IsTrue(payload.AuthRequired);
            CollectionAssert.Contains(payload.FailedProviderKeys, "Steam");
            Assert.AreEqual(0, cache.SaveFriendListCalls);
        }

        private static RefreshRuntime CreateRuntime(
            FakeFriendCache cache,
            bool enableParallelProviderRefresh = true,
            Action<PlayniteAchievementsSettings> configureSettings = null,
            IEnumerable<IDataProvider> providers = null,
            IEnumerable<Game> playniteGames = null)
        {
            var settings = new PlayniteAchievementsSettings();
            settings.Persisted.EnableParallelProviderRefresh = enableParallelProviderRefresh;
            settings.Persisted.ScanDelayMs = 0;
            settings.Persisted.MaxRetryAttempts = 0;
            configureSettings?.Invoke(settings);

            var providerList = providers?.Where(provider => provider != null).ToList();
            return providerList?.Count > 0
                ? new RefreshRuntime(
                    cache,
                    settings,
                    api: new FakePlayniteApi(playniteGames ?? Array.Empty<Game>()),
                    providers: providerList,
                    refreshOrder: providerList.Select(provider => provider.ProviderKey))
                : new RefreshRuntime(
                    cache,
                    settings);
        }

        private static void ConfigureExophaseSteamFriendOwnership(
            PlayniteAchievementsSettings settings,
            string steamExternalUserId,
            string exophaseExternalUserId)
        {
            settings.Persisted.UseExophaseForSteamFriendOwnership = true;
            settings.Persisted.AddOrUpdateFriend(
                "Steam",
                steamExternalUserId,
                "Steam Friend",
                null,
                null,
                FriendSettingsSource.Manual);
            settings.Persisted.AddOrUpdateFriend(
                "Exophase",
                exophaseExternalUserId,
                "Exophase Friend",
                null,
                null,
                FriendSettingsSource.Manual,
                new[] { "steam" });
            settings.Persisted.AddOrUpdateFriendMergeGroup(new[]
            {
                FriendAccountRef.From("Steam", steamExternalUserId),
                FriendAccountRef.From("Exophase", exophaseExternalUserId)
            });
        }

        private static FriendIdentity MakeFriend(string externalUserId) =>
            MakeFriend(externalUserId, "Steam");

        private static FriendIdentity MakeFriend(string externalUserId, string providerKey) =>
            new FriendIdentity
            {
                ProviderKey = providerKey,
                ExternalUserId = externalUserId,
                DisplayName = "Friend " + externalUserId
            };

        private static void SeedCachedFriends(
            FakeFriendCache cache,
            string providerKey,
            params string[] externalUserIds)
        {
            if (cache == null)
            {
                return;
            }

            cache.CachedFriends = (externalUserIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => MakeFriend(id.Trim(), providerKey))
                .ToList();
        }

        private static FriendRefreshCandidate MakeCandidate(string externalUserId, int appId) =>
            new FriendRefreshCandidate
            {
                Friend = MakeFriend(externalUserId),
                AppId = appId,
                GameName = "Game " + appId
            };

        private sealed class ProgressEvent
        {
            public ProgressEvent(string message, int current, int total)
            {
                Message = message;
                Current = current;
                Total = total;
            }

            public string Message { get; }
            public int Current { get; }
            public int Total { get; }
        }

        private sealed class FakeFriendCache : ICacheManager, IFriendCacheManager
        {
            private int _saveFriendListCalls;
            private int _saveFriendOwnershipCalls;
            private int _saveFriendGameAchievementsCalls;
            private int _saveFriendGameDefinitionCalls;
            private int _saveProviderOnlyOwnershipCalls;
            private int _promoteProviderOnlyGameCalls;
            private readonly List<FriendGameOwnership> _savedOwnershipRows = new List<FriendGameOwnership>();
            private readonly List<FriendOwnershipSaveOptions> _savedOwnershipOptions = new List<FriendOwnershipSaveOptions>();

            public List<FriendRefreshCandidate> Candidates { get; set; } = new List<FriendRefreshCandidate>();
            public Dictionary<string, FriendGameDefinitionState> DefinitionStates { get; set; } =
                new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, GameAchievementData> CachedGameData { get; set; } =
                new Dictionary<string, GameAchievementData>(StringComparer.OrdinalIgnoreCase);
            public bool ProviderGamesMappedToPlayniteLibrary { get; set; } = true;
            public int SaveFriendListCalls => _saveFriendListCalls;
            public int SaveFriendOwnershipCalls => _saveFriendOwnershipCalls;
            public int SaveFriendGameAchievementsCalls => _saveFriendGameAchievementsCalls;
            public int SaveFriendGameDefinitionCalls => _saveFriendGameDefinitionCalls;
            public int SaveProviderOnlyOwnershipCalls => _saveProviderOnlyOwnershipCalls;
            public int PromoteProviderOnlyGameCalls => _promoteProviderOnlyGameCalls;
            public FriendRefreshOptions LastLoadOptions { get; private set; }
            public IReadOnlyList<FriendGameOwnership> SavedOwnershipRows
            {
                get
                {
                    lock (_savedOwnershipRows)
                    {
                        return _savedOwnershipRows.ToList();
                    }
                }
            }
            public IReadOnlyList<FriendOwnershipSaveOptions> SavedOwnershipOptions
            {
                get
                {
                    lock (_savedOwnershipOptions)
                    {
                        return _savedOwnershipOptions.ToList();
                    }
                }
            }
            public List<FriendIdentity> CachedFriends { get; set; } = new List<FriendIdentity>();

            public event EventHandler<FriendCacheInvalidatedEventArgs> FriendCacheInvalidated;

            public IFriendCacheInvalidationBatch BeginFriendCacheInvalidationBatch() =>
                NullFriendCacheInvalidationBatch.Instance;

            public void NotifyFriendCacheInvalidated()
            {
                FriendCacheInvalidated?.Invoke(this, FriendCacheInvalidatedEventArgs.FullInvalidation);
            }

            public FriendCacheWriteResult SaveFriendList(string providerKey, IReadOnlyList<FriendIdentity> friends)
            {
                Interlocked.Increment(ref _saveFriendListCalls);
                var count = friends?.Count ?? 0;
                return FriendCacheWriteResult.Ok(count, count, 0);
            }

            public FriendCacheWriteResult SaveFriendOwnership(
                string providerKey,
                string externalUserId,
                IReadOnlyList<FriendGameOwnership> ownership,
                FriendOwnershipSaveOptions options = null)
            {
                Interlocked.Increment(ref _saveFriendOwnershipCalls);
                if (options?.IncludeProviderOnlyGames == true)
                {
                    Interlocked.Increment(ref _saveProviderOnlyOwnershipCalls);
                }

                lock (_savedOwnershipOptions)
                {
                    _savedOwnershipOptions.Add(new FriendOwnershipSaveOptions
                    {
                        IncludeProviderOnlyGames = options?.IncludeProviderOnlyGames == true,
                        PruneStaleShared = options?.PruneStaleShared == true
                    });
                }

                lock (_savedOwnershipRows)
                {
                    _savedOwnershipRows.AddRange((ownership ?? Array.Empty<FriendGameOwnership>())
                        .Where(item => item != null));
                }

                var count = ownership?.Count ?? 0;
                return FriendCacheWriteResult.Ok(count, count, 0);
            }

            public FriendCacheWriteResult SaveFriendGameDefinition(
                string providerKey,
                FriendGameDefinition definition)
            {
                Interlocked.Increment(ref _saveFriendGameDefinitionCalls);
                return FriendCacheWriteResult.Ok(definition?.Achievements?.Count ?? 0, definition?.Achievements?.Count ?? 0, 0);
            }

            public FriendCacheWriteResult SaveProviderGameImagePaths(
                string providerKey,
                string providerGameKey,
                int appId,
                string iconAbsolutePath,
                string coverAbsolutePath) =>
                FriendCacheWriteResult.Ok(1, 1, 0);

            public Dictionary<string, FriendGameDefinitionState> LoadFriendGameDefinitionStates(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                DefinitionStates
                    .Where(pair => providerGameKeys?.Contains(pair.Key) == true)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            public List<string> LoadLegacyKeyedDefinitionGameKeys(
                string providerKey,
                IReadOnlyCollection<string> providerGameKeys) =>
                new List<string>();

            public FriendUnownedCacheStats GetUnownedFriendGameCacheStats() =>
                new FriendUnownedCacheStats();

            public FriendUnownedCacheClearResult ClearUnownedFriendGameData() =>
                new FriendUnownedCacheClearResult { Success = true };

            public FriendCacheWriteResult ClearUnownedFriendGame(string providerKey, int appId, string providerGameKey) =>
                FriendCacheWriteResult.Ok();

            public bool IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey) =>
                ProviderGamesMappedToPlayniteLibrary;

            public List<FriendGameMapping> FriendGameMappings { get; set; } = new List<FriendGameMapping>();

            public IReadOnlyList<FriendGameMapping> LoadFriendGameMappings(string providerKey) =>
                FriendGameMappings.ToList();

            public FriendCacheWriteResult PromoteProviderOnlyGameToPlayniteBacked(
                string providerKey,
                int appId,
                string providerGameKey,
                Guid playniteGameId)
            {
                Interlocked.Increment(ref _promoteProviderOnlyGameCalls);
                return FriendCacheWriteResult.Ok(1, 1, 0);
            }

            public FriendCacheWriteResult SaveFriendGameAchievements(
                string providerKey,
                string externalUserId,
                string providerGameKey,
                int appId,
                FriendGameAchievements achievements)
            {
                Interlocked.Increment(ref _saveFriendGameAchievementsCalls);
                return FriendCacheWriteResult.Ok();
            }

            public List<FriendAchievementRow> LoadFriendGameAchievements(
                string providerKey,
                string externalUserId,
                int appId,
                string providerGameKey) =>
                new List<FriendAchievementRow>();

            public FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false) =>
                FriendCacheWriteResult.Ok();

            public List<FriendIdentity> LoadFriendIdentities(string providerKey) =>
                CachedFriends.ToList();

            public DateTime? GetMostRecentFriendLastRefreshedUtc() => null;

            public List<FriendRefreshCandidate> LoadFriendRefreshCandidates(
                string providerKey,
                FriendRefreshOptions options)
            {
                LastLoadOptions = options?.Clone();
                return Candidates.ToList();
            }

            public IReadOnlyDictionary<string, FriendOwnershipRecency> OwnershipRecency { get; set; } =
                new Dictionary<string, FriendOwnershipRecency>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyDictionary<string, FriendOwnershipRecency> LoadFriendOwnershipRecency(
                string providerKey,
                string externalUserId) => OwnershipRecency;

            public FriendsOverviewData LoadFriendsOverviewData(int recentLimit) =>
                new FriendsOverviewData();

            public FriendsOverviewData LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes) =>
                new FriendsOverviewData();

            public FriendsOverviewData LoadFriendGameAchievementData(Guid playniteGameId) =>
                new FriendsOverviewData();

            public FriendsOverviewData LoadFriendGameAchievementData(FriendCacheChange gameScope) =>
                new FriendsOverviewData();

            public FriendsOverviewData LoadFriendRecentUnlocksData(int recentLimit) =>
                new FriendsOverviewData();

            public IReadOnlyList<CurrentUserGameLabel> CurrentUserGameLabels { get; set; } =
                new List<CurrentUserGameLabel>();

            public IReadOnlyList<CurrentUserGameLabel> LoadCurrentUserGameLabels() => CurrentUserGameLabels;

#pragma warning disable CS0067
            public event EventHandler<GameCacheUpdatedEventArgs> GameCacheUpdated;
            public event EventHandler<CacheDeltaEventArgs> CacheDeltaUpdated;
            public event EventHandler<CacheInvalidatedEventArgs> CacheInvalidated;
#pragma warning restore CS0067

            public void EnsureDiskCacheOrClearMemory() { }
            public bool CacheFileExists() => false;
            public bool IsCacheValid() => true;
            public DateTime? GetMostRecentLastUpdatedUtc() => null;
            public List<string> GetCachedGameIds() => new List<string>();
            public GameAchievementData LoadGameData(string key) =>
                !string.IsNullOrWhiteSpace(key) && CachedGameData.TryGetValue(key, out var data)
                    ? data
                    : null;
            public CacheWriteResult SaveGameData(string key, GameAchievementData data) => null;
            public void RemoveGameData(Guid playniteGameId) { }
            public void RemoveGameCache(Guid playniteGameId) { }
            public void NotifyCacheInvalidated() =>
                CacheInvalidated?.Invoke(this, CacheInvalidatedEventArgs.FullInvalidation);
            public void NotifyCacheInvalidated(IReadOnlyList<Guid> changedGameIds) =>
                CacheInvalidated?.Invoke(this, CacheInvalidatedEventArgs.Scoped(changedGameIds));
            public void ClearCache() { }
            public string ExportDatabaseToCsv(string exportDirectory) => null;
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public FakePlayniteApi(IEnumerable<Game> games)
            {
                Database = new FakeGameDatabase(games);
            }

            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database { get; }
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths => null;
            public INotificationsAPI Notifications => null;
            public IPlayniteInfoAPI ApplicationInfo => null;
            public IWebViewFactory WebViews => null;
            public IResourceProvider Resources => null;
            public IUriHandlerAPI UriHandler => null;
            public IPlayniteSettingsAPI ApplicationSettings => null;
            public IAddons Addons => null;
            public IEmulationAPI Emulation => null;

            public string ExpandGameVariables(Game game, string source) => source;
            public string ExpandGameVariables(Game game, string source, string fallbackValue) => source ?? fallbackValue;
            public GameAction ExpandGameVariables(Game game, GameAction source) => source;
            public void StartGame(Guid id) { }
            public void InstallGame(Guid id) { }
            public void UninstallGame(Guid id) { }
            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args) { }
            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args) { }
            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args) { }
        }

        private sealed class FakeGameDatabase : IGameDatabaseAPI
        {
            public FakeGameDatabase(IEnumerable<Game> games)
            {
                Games = new FakeGameCollection(games);
            }

            public IItemCollection<Game> Games { get; }
            public IItemCollection<Platform> Platforms => null;
            public IItemCollection<Emulator> Emulators => null;
            public IItemCollection<Genre> Genres => null;
            public IItemCollection<Company> Companies => null;
            public IItemCollection<Tag> Tags => null;
            public IItemCollection<Category> Categories => null;
            public IItemCollection<Series> Series => null;
            public IItemCollection<AgeRating> AgeRatings => null;
            public IItemCollection<Region> Regions => null;
            public IItemCollection<GameSource> Sources => null;
            public IItemCollection<GameFeature> Features => null;
            public IItemCollection<GameScannerConfig> GameScanners => null;
            public IItemCollection<CompletionStatus> CompletionStatuses => null;
            public IItemCollection<ImportExclusionItem> ImportExclusions => null;
            public IItemCollection<FilterPreset> FilterPresets => null;
            public bool IsOpen => true;
            public string DatabasePath => string.Empty;
            public event EventHandler DatabaseOpened { add { } remove { } }
            public string AddFile(string path, Guid parentId) => path;
            public void SaveFile(string path, string sourceFile) { }
            public void RemoveFile(string path) { }
            public IDisposable BufferedUpdate() => NullDisposable.Instance;
            public void BeginBufferUpdate() { }
            public void EndBufferUpdate() { }
            public string GetFileStoragePath(Guid parentId) => string.Empty;
            public string GetFullFilePath(string path) => path;
            public Game ImportGame(GameMetadata gameMetadata) => null;
            public Game ImportGame(GameMetadata gameMetadata, LibraryPlugin libraryPlugin) => null;
            public bool GetGameMatchesFilter(Game game, FilterPresetSettings filter) => false;
            public IEnumerable<Game> GetFilteredGames(FilterPresetSettings filter) => Enumerable.Empty<Game>();
            public bool GetGameMatchesFilter(Game game, FilterPresetSettings filter, bool ignoreHidden) => false;
            public IEnumerable<Game> GetFilteredGames(FilterPresetSettings filter, bool ignoreHidden) => Enumerable.Empty<Game>();
        }

        private sealed class FakeGameCollection : IItemCollection<Game>
        {
            private readonly Dictionary<Guid, Game> _items;

            public FakeGameCollection(IEnumerable<Game> games)
            {
                _items = (games ?? Enumerable.Empty<Game>())
                    .Where(game => game != null)
                    .ToDictionary(game => game.Id, game => game);
            }

            public GameDatabaseCollection CollectionType => GameDatabaseCollection.Games;
            public int Count => _items.Count;
            public bool IsReadOnly => false;
            public Game this[Guid id] { get => Get(id); set => _items[id] = value; }
#pragma warning disable CS0067
            public event EventHandler<ItemCollectionChangedEventArgs<Game>> ItemCollectionChanged;
            public event EventHandler<ItemUpdatedEventArgs<Game>> ItemUpdated;
#pragma warning restore CS0067
            public bool ContainsItem(Guid id) => _items.ContainsKey(id);
            public Game Get(Guid id) => _items.TryGetValue(id, out var game) ? game : null;
            public List<Game> Get(IList<Guid> ids) => ids?.Select(Get).Where(item => item != null).ToList() ?? new List<Game>();
            public Game Add(string name) => throw new NotSupportedException();
            public Game Add(string name, Func<Game, string, bool> mergeAction) => throw new NotSupportedException();
            public IEnumerable<Game> Add(List<string> items) => throw new NotSupportedException();
            public Game Add(MetadataProperty item) => throw new NotSupportedException();
            public IEnumerable<Game> Add(IEnumerable<MetadataProperty> items) => throw new NotSupportedException();
            public IEnumerable<Game> Add(List<string> items, Func<Game, string, bool> mergeAction) => throw new NotSupportedException();
            public void Add(IEnumerable<Game> items) => Update(items);
            public bool Remove(Guid id) => _items.Remove(id);
            public bool Remove(IEnumerable<Game> items)
            {
                var removed = false;
                foreach (var item in items ?? Enumerable.Empty<Game>())
                {
                    removed |= item != null && _items.Remove(item.Id);
                }

                return removed;
            }

            public void Update(Game item)
            {
                if (item != null)
                {
                    _items[item.Id] = item;
                }
            }

            public void Update(IEnumerable<Game> items)
            {
                foreach (var item in items ?? Enumerable.Empty<Game>())
                {
                    Update(item);
                }
            }

            public IDisposable BufferedUpdate() => NullDisposable.Instance;
            public void BeginBufferUpdate() { }
            public void EndBufferUpdate() { }
            public IEnumerable<Game> GetClone() => _items.Values.ToList();
            public void Add(Game item) => Update(item);
            public void Clear() => _items.Clear();
            public bool Contains(Game item) => item != null && _items.ContainsKey(item.Id);
            public void CopyTo(Game[] array, int arrayIndex) => _items.Values.ToList().CopyTo(array, arrayIndex);
            public bool Remove(Game item) => item != null && _items.Remove(item.Id);
            public IEnumerator<Game> GetEnumerator() => _items.Values.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Dispose() { }
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new NullDisposable();
            public void Dispose() { }
        }

        private sealed class FakeDataProvider : IDataProvider, IRefreshAuthContextReceiver
        {
            private readonly Func<Game, bool> _isCapable;
            private int _refreshCalls;
            private int _beginAuthContextCalls;
            private int _endAuthContextCalls;
            private readonly List<Guid> _refreshedGameIds = new List<Guid>();

            public FakeDataProvider(string providerKey, IFriendsProvider friends, Func<Game, bool> isCapable = null)
            {
                ProviderKey = providerKey;
                Friends = friends;
                _isCapable = isCapable ?? (_ => true);
            }

            public string ProviderName => ProviderKey;
            public string ProviderKey { get; }
            public string ProviderIconKey => ProviderKey;
            public string ProviderColorHex => "#000000";
            public bool IsAuthenticated => true;
            public ISessionManager AuthSession => null;
            public IFriendsProvider Friends { get; }
            public Action<string> OperationLog { get; set; }
            public int RefreshCalls => _refreshCalls;
            public int BeginAuthContextCalls => _beginAuthContextCalls;
            public int EndAuthContextCalls => _endAuthContextCalls;
            public IReadOnlyList<Guid> RefreshedGameIds
            {
                get
                {
                    lock (_refreshedGameIds)
                    {
                        return _refreshedGameIds.ToList();
                    }
                }
            }
            public bool IsCapable(Game game) => _isCapable(game);

            public async Task<RebuildPayload> RefreshAsync(
                IReadOnlyList<Game> gamesToRefresh,
                Action<Game> onGameStarting,
                Func<Game, GameAchievementData, Task> onGameCompleted,
                CancellationToken cancel)
            {
                Interlocked.Increment(ref _refreshCalls);
                OperationLog?.Invoke($"{ProviderKey}:current");

                var summary = new RebuildSummary();
                foreach (var game in gamesToRefresh ?? Array.Empty<Game>())
                {
                    cancel.ThrowIfCancellationRequested();
                    onGameStarting?.Invoke(game);
                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, null).ConfigureAwait(false);
                    }

                    summary.GamesRefreshed++;
                    summary.GamesWithoutAchievements++;
                    if (game?.Id != Guid.Empty)
                    {
                        summary.RefreshedGameIds.Add(game.Id);
                        lock (_refreshedGameIds)
                        {
                            _refreshedGameIds.Add(game.Id);
                        }
                    }
                }

                return new RebuildPayload { Summary = summary };
            }

            public IProviderSettings GetSettings() => null;
            public void ApplySettings(IProviderSettings settings) { }
            public ProviderSettingsViewBase CreateSettingsView() => null;
            public void BeginRefreshAuthContext(RefreshAuthContext context)
            {
                Interlocked.Increment(ref _beginAuthContextCalls);
            }

            public void EndRefreshAuthContext(RefreshAuthContext context)
            {
                Interlocked.Increment(ref _endAuthContextCalls);
            }
        }

        private sealed class FakeFriendsProvider : IFriendsProvider, ISteamFriendOwnershipSupplementSource
        {
            private int _beginCalls;
            private int _endCalls;
            private int _getFriendsCalls;
            private int _getOwnedGamesCalls;
            private int _getSteamOwnedGamesCalls;
            private int _getFriendGameAchievementsCalls;
            private int _getFriendGameDefinitionCalls;
            private int _currentOwnershipCalls;
            private int _maxConcurrentOwnershipCalls;
            private int _currentAchievementCalls;
            private int _maxConcurrentAchievementCalls;
            private readonly List<int> _definitionAppIds = new List<int>();
            private readonly List<string> _networkCallOrder = new List<string>();

            public FakeFriendsProvider(string providerKey)
            {
                ProviderKey = providerKey;
                BeginResult = FriendsProviderResult<FriendsRefreshPreparation>
                    .FromData(new FriendsRefreshPreparation { CanRefreshAchievements = true });
            }

            public string ProviderKey { get; }
            public FriendsProviderResult<FriendsRefreshPreparation> BeginResult { get; set; }
            public IReadOnlyList<FriendIdentity> FriendsToReturn { get; set; } = Array.Empty<FriendIdentity>();
            public IReadOnlyList<FriendGameOwnership> OwnedGamesToReturn { get; set; }
            public FriendsProviderResult<IReadOnlyList<FriendGameOwnership>> OwnedGamesResult { get; set; }
            public IReadOnlyList<FriendGameOwnership> SteamOwnedGamesSupplementToReturn { get; set; }
            public IReadOnlyList<FriendGameOwnership> LastKnownSteamOwnership { get; private set; }
            public FriendsProviderResult<IReadOnlyList<FriendGameOwnership>> SteamOwnedGamesSupplementResult { get; set; }
            public FriendGameDefinitionStatus DefinitionStatusToReturn { get; set; } = FriendGameDefinitionStatus.Ok;
            public List<FriendAchievementRow> AchievementRowsToReturn { get; set; }
            // Per-friend achievement rows keyed by ExternalUserId; falls back to AchievementRowsToReturn.
            public Dictionary<string, List<FriendAchievementRow>> AchievementRowsByFriend { get; set; }
            public Action<string> OperationLog { get; set; }
            public int OwnershipDelayMs { get; set; }
            public int AchievementDelayMs { get; set; }
            public int BeginCalls => _beginCalls;
            public int EndCalls => _endCalls;
            public int GetFriendsCalls => _getFriendsCalls;
            public int GetOwnedGamesCalls => _getOwnedGamesCalls;
            public int GetSteamOwnedGamesCalls => _getSteamOwnedGamesCalls;
            public int GetFriendGameAchievementsCalls => _getFriendGameAchievementsCalls;
            public int GetFriendGameDefinitionCalls => _getFriendGameDefinitionCalls;
            public int MaxConcurrentOwnershipCalls => _maxConcurrentOwnershipCalls;
            public int MaxConcurrentAchievementCalls => _maxConcurrentAchievementCalls;
            public IReadOnlyList<int> DefinitionAppIds
            {
                get
                {
                    lock (_definitionAppIds)
                    {
                        return _definitionAppIds.ToList();
                    }
                }
            }

            // Ordered log of achievement/definition network calls, e.g. "achievements:1", "definition:100".
            public IReadOnlyList<string> NetworkCallOrder
            {
                get
                {
                    lock (_networkCallOrder)
                    {
                        return _networkCallOrder.ToList();
                    }
                }
            }

            private void RecordNetworkCall(string entry)
            {
                lock (_networkCallOrder)
                {
                    _networkCallOrder.Add(entry);
                }
            }

            public Task<FriendsProviderResult<FriendsRefreshPreparation>> BeginRefreshAsync(CancellationToken cancel)
            {
                Interlocked.Increment(ref _beginCalls);
                OperationLog?.Invoke($"{ProviderKey}:friend-begin");
                return Task.FromResult(BeginResult);
            }

            public void EndRefresh()
            {
                Interlocked.Increment(ref _endCalls);
            }

            public Task<FriendsProviderResult<IReadOnlyList<FriendIdentity>>> GetFriendsAsync(CancellationToken cancel)
            {
                Interlocked.Increment(ref _getFriendsCalls);
                return Task.FromResult(FriendsProviderResult<IReadOnlyList<FriendIdentity>>.FromData(FriendsToReturn));
            }

            public async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetOwnedGamesAsync(
                FriendIdentity friend,
                CancellationToken cancel)
            {
                Interlocked.Increment(ref _getOwnedGamesCalls);
                var current = Interlocked.Increment(ref _currentOwnershipCalls);
                UpdateMaxConcurrentOwnershipCalls(current);
                try
                {
                    if (OwnershipDelayMs > 0)
                    {
                        await Task.Delay(OwnershipDelayMs, cancel).ConfigureAwait(false);
                    }

                    if (OwnedGamesResult != null)
                    {
                        return OwnedGamesResult;
                    }

                    IReadOnlyList<FriendGameOwnership> ownedGames = OwnedGamesToReturn ?? new[]
                    {
                        new FriendGameOwnership
                        {
                            ExternalUserId = friend?.ExternalUserId,
                            ProviderKey = ProviderKey,
                            AppId = 100,
                            PlaytimeForeverMinutes = 1
                        }
                    };

                    return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(ownedGames);
                }
                finally
                {
                    Interlocked.Decrement(ref _currentOwnershipCalls);
                }
            }

            public Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetSteamOwnedGamesAsync(
                string externalUserId,
                IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
                IReadOnlyList<FriendGameOwnership> knownSteamOwnership,
                CancellationToken cancel)
            {
                Interlocked.Increment(ref _getSteamOwnedGamesCalls);
                LastKnownSteamOwnership = knownSteamOwnership;
                if (SteamOwnedGamesSupplementResult != null)
                {
                    return Task.FromResult(SteamOwnedGamesSupplementResult);
                }

                return Task.FromResult(FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(
                    SteamOwnedGamesSupplementToReturn ?? Array.Empty<FriendGameOwnership>()));
            }

            public async Task<FriendsProviderResult<FriendGameAchievements>> GetFriendGameAchievementsAsync(
                FriendIdentity friend,
                string providerGameKey,
                int appId,
                string gameName,
                CancellationToken cancel)
            {
                Interlocked.Increment(ref _getFriendGameAchievementsCalls);
                RecordNetworkCall($"achievements:{friend?.ExternalUserId}");
                var current = Interlocked.Increment(ref _currentAchievementCalls);
                UpdateMaxConcurrentAchievementCalls(current);
                try
                {
                    if (AchievementDelayMs > 0)
                    {
                        await Task.Delay(AchievementDelayMs, cancel).ConfigureAwait(false);
                    }

                    var rows = AchievementRowsByFriend != null &&
                               friend?.ExternalUserId != null &&
                               AchievementRowsByFriend.TryGetValue(friend.ExternalUserId, out var perFriendRows)
                        ? perFriendRows
                        : AchievementRowsToReturn;
                    return FriendsProviderResult<FriendGameAchievements>.FromData(
                        new FriendGameAchievements
                        {
                            Friend = friend,
                            AppId = appId,
                            ProviderGameKey = providerGameKey,
                            LastUpdatedUtc = DateTime.UtcNow,
                            Rows = rows?
                                .Select(row => new FriendAchievementRow
                                {
                                    ApiName = row.ApiName,
                                    DisplayName = row.DisplayName,
                                    Description = row.Description,
                                    IconUrl = row.IconUrl,
                                    UnlockedIconUrl = row.UnlockedIconUrl,
                                    LockedIconUrl = row.LockedIconUrl,
                                    Points = row.Points,
                                    ScaledPoints = row.ScaledPoints,
                                    TrophyType = row.TrophyType,
                                    Hidden = row.Hidden,
                                    IsCapstone = row.IsCapstone,
                                    GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                                    Rarity = row.Rarity,
                                    Unlocked = row.Unlocked,
                                    UnlockTimeUtc = row.UnlockTimeUtc,
                                    ProgressNum = row.ProgressNum,
                                    ProgressDenom = row.ProgressDenom
                                })
                                .ToList() ?? new List<FriendAchievementRow>()
                        });
                }
                finally
                {
                    Interlocked.Decrement(ref _currentAchievementCalls);
                }
            }

            public Task<FriendsProviderResult<FriendGameDefinition>> GetFriendGameDefinitionAsync(
                string providerGameKey,
                int appId,
                string gameName,
                CancellationToken cancel)
            {
                Interlocked.Increment(ref _getFriendGameDefinitionCalls);
                RecordNetworkCall($"definition:{appId}");
                lock (_definitionAppIds)
                {
                    _definitionAppIds.Add(appId);
                }

                return Task.FromResult(FriendsProviderResult<FriendGameDefinition>.FromData(new FriendGameDefinition
                {
                    ProviderKey = ProviderKey,
                    AppId = appId,
                    ProviderGameKey = providerGameKey,
                    GameName = gameName,
                    Status = DefinitionStatusToReturn,
                    LastCheckedUtc = DateTime.UtcNow,
                    Achievements = DefinitionStatusToReturn == FriendGameDefinitionStatus.Ok
                        ? new List<AchievementDetail>
                        {
                            new AchievementDetail { ApiName = "A", DisplayName = "A" }
                        }
                        : new List<AchievementDetail>()
                }));
            }

            private void UpdateMaxConcurrentAchievementCalls(int current)
            {
                while (true)
                {
                    var existing = Volatile.Read(ref _maxConcurrentAchievementCalls);
                    if (current <= existing)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _maxConcurrentAchievementCalls, current, existing) == existing)
                    {
                        return;
                    }
                }
            }

            private void UpdateMaxConcurrentOwnershipCalls(int current)
            {
                while (true)
                {
                    var existing = Volatile.Read(ref _maxConcurrentOwnershipCalls);
                    if (current <= existing)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _maxConcurrentOwnershipCalls, current, existing) == existing)
                    {
                        return;
                    }
                }
            }
        }
    }
}
