using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class MetadataOverlayTests
    {
        private string _directory;

        [TestInitialize]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pa-metadata-overlay-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [TestMethod]
        public async Task Icons_ComeFromThePreferredSource_AndReportTheSourceChange()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons | MetadataField.CapstoneArt)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png", locked: "steam_gray.png"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));

            bool changed;
            using (var run = overlay.BeginRun())
            {
                changed = await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.IsTrue(changed);
            Assert.AreEqual("https://psn/dragon.png", data.Achievements[0].UnlockedIconPath);
            Assert.IsNull(data.Achievements[0].LockedIconPath);
            Assert.AreEqual(1, playstation.BeginCount);
            Assert.AreEqual(1, playstation.EndCount);

            // A second refresh with the same source keeps the cached icon files.
            var next = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"));
            next.Game = data.Game;
            next.PlayniteGameId = data.PlayniteGameId;
            using (var run = overlay.BeginRun())
            {
                Assert.IsFalse(await run.ApplyAsync(next.Game, next, CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task GameChoices_OptInAndOutOverThePlatform()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons | MetadataField.TrophyTypes);
            var gold = Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png");
            gold.TrophyType = "gold";
            playstation.Rows.Add(gold);
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            // The platform takes PlayStation trophy types but not its icons.
            var overlay = Overlay(new[] { playstation }, Choices(trophyTypes: "PlayStation"));
            var gameId = data.PlayniteGameId.Value;

            Assert.IsTrue(overlay.Links.SetChoices(gameId, new MetadataChoices { Icons = "PlayStation", TrophyTypes = string.Empty }));
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("https://psn/dragon.png", data.Achievements[0].UnlockedIconPath, "The game opts in to PlayStation icons.");
            Assert.IsNull(data.Achievements[0].TrophyType, "The game's Own trophy types win over the platform's.");

            Assert.IsTrue(overlay.Links.SetChoices(gameId, new MetadataChoices()));
            Assert.IsNull(overlay.Links.GetChoices(gameId), "Empty choices follow the platform again.");
        }

        [TestMethod]
        public void GameChoices_TravelInPortableData()
        {
            var file = new GameCustomDataFile
            {
                PlayniteGameId = Guid.NewGuid(),
                MetadataChoices = new MetadataChoices { Icons = "PlayStation", Points = "Xbox" }
            };

            var restored = GameCustomDataFile.FromPortable(file.ToPortable(), file.PlayniteGameId, null, null);

            Assert.IsTrue(file.MetadataChoices.SameAs(restored.MetadataChoices));
            Assert.IsTrue(GameCustomDataNormalizer.HasPortableData(file.ToPortable()));
        }

        [TestMethod]
        public async Task CategoryArt_IsSavedOnlyForCategoriesTheSourceGave()
        {
            var source = new FakeSource("SteamHunters", MetadataField.Categories)
            {
                Rows = { Row("p1", "Far From Home", category: "Far Harbor", type: "DLC") },
                CategoryArt =
                {
                    ["Far Harbor"] = "https://art/far.png",
                    ["Nuka-World"] = "https://art/nuka.png"
                }
            };
            var data = Game("Steam", Row("ACH_FAR", "Far From Home"));
            var saved = new List<(string Label, string ImageUrl)>();
            var overlay = Overlay(
                new[] { source },
                Choices(categories: "SteamHunters"),
                (gameId, art, ct) =>
                {
                    saved.AddRange(art);
                    return Task.CompletedTask;
                });

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Far Harbor", data.Achievements[0].Category);
            CollectionAssert.AreEqual(new[] { ("Far Harbor", "https://art/far.png") }, saved);
        }

        [TestMethod]
        public async Task TrophyTypesAndPoints_ComeFromTheirChosenSources()
        {
            var playstationPlatinum = Row("p1", "Master");
            playstationPlatinum.TrophyType = "platinum";
            playstationPlatinum.Points = 300;
            var playstationGold = Row("p2", "Dragon Slayer");
            playstationGold.TrophyType = "gold";
            playstationGold.Points = 90;
            var playstation = new FakeSource("PlayStation", MetadataField.TrophyTypes)
            {
                Rows = { playstationPlatinum, playstationGold }
            };
            var xboxGold = Row("x2", "Dragon Slayer");
            xboxGold.Points = 50;
            var xbox = new FakeSource("Xbox", MetadataField.Points) { Rows = { xboxGold } };
            var master = Row("ACH_MASTER", "Master");
            master.Points = 10;
            var dragon = Row("ACH_DRAGON", "Dragon Slayer");
            var bonus = Row("ACH_BONUS", "Bonus");
            bonus.TrophyType = "bronze";
            var data = Game("Steam", master, dragon, bonus);
            playstation.Rows.Add(Row("p3", "Bonus"));
            playstation.Rows[2].TrophyType = "gold";
            var overlay = Overlay(new IMetadataProvider[] { playstation, xbox }, Choices(trophyTypes: "PlayStation", points: "Xbox"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("platinum", master.TrophyType);
            Assert.IsFalse(master.IsCapstone, "A borrowed platinum type only labels the trophy.");
            Assert.AreEqual(10, master.Points, "An achievement's own points are kept.");
            Assert.AreEqual("gold", dragon.TrophyType);
            Assert.AreEqual(50, dragon.Points, "Points the platform lacks come from the source chosen for points.");
            Assert.AreEqual("bronze", bonus.TrophyType, "An achievement's own trophy type is kept.");
        }

        [TestMethod]
        public void PlatformsWithTheirOwnPointsOrTrophyTypes_TakeNone()
        {
            var settings = new PersistedSettings();
            var game = new MetadataChoices { Points = MetadataChoices.Xbox, TrophyTypes = MetadataChoices.PlayStation };

            var epic = settings.ResolveMetadataChoices("Epic", game);
            var retro = settings.ResolveMetadataChoices("RetroAchievements");
            var steam = settings.ResolveMetadataChoices("Steam");

            Assert.AreEqual(string.Empty, epic.Points);
            Assert.AreEqual(MetadataChoices.PlayStation, epic.TrophyTypes, "Epic has points of its own but no trophy types.");
            Assert.AreEqual(string.Empty, retro.Points);
            Assert.AreEqual(string.Empty, retro.TrophyTypes);
            Assert.AreEqual(MetadataChoices.Xbox, steam.Points);
            Assert.AreEqual(MetadataChoices.PlayStation, steam.TrophyTypes);
        }

        [TestMethod]
        public void MetadataDefaults_FillFieldsAnOlderConfigurationLacks()
        {
            var settings = new PersistedSettings
            {
                MetadataDefaults = new MetadataChoices { Icons = "PlayStation", Categories = string.Empty, Rarity = string.Empty }
            };

            Assert.AreEqual("PlayStation", settings.MetadataDefaults.Icons);
            Assert.AreEqual(MetadataChoices.PlayStation, settings.MetadataDefaults.TrophyTypes);
            Assert.AreEqual(MetadataChoices.Xbox, settings.MetadataDefaults.Points);
        }

        [TestMethod]
        public async Task UserLink_SurvivesARefreshWhoseSourceReturnsNothing()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons);
            var data = Game("Steam", Row("ACH_ONE", "One", icon: "steam.png"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            var gameId = data.PlayniteGameId.Value;

            Assert.IsTrue(overlay.Links.SetUser(gameId, "PlayStation", "tokyodark-remembrance-psn"));
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
                run.RestoreOwnIconsWhereDownloadFailed(data, path => false);
            }

            Assert.IsTrue(overlay.Links.TryGet(gameId, "PlayStation", out var record));
            Assert.AreEqual("tokyodark-remembrance-psn", record.Id);
            Assert.AreEqual(MetadataLinkOrigin.User, record.Origin);
        }

        [TestMethod]
        public async Task FailedBorrowedIcon_FallsBackToOwn_AndIsTriedAgainNextRefresh()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows =
                {
                    Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png"),
                    Row("p2", "Curiosity", icon: "https://psn/curiosity.png")
                }
            };
            var data = Game(
                "Steam",
                Row("ACH_DRAGON", "Dragon Slayer", icon: "steam_dragon.png"),
                Row("ACH_CURIOUS", "Curiosity", icon: "steam_curious.png", locked: "steam_curious_gray.png"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            bool Undownloaded(string path) => path != null && path.StartsWith("https://", StringComparison.Ordinal);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
                data.Achievements[0].UnlockedIconPath = "cache/dragon.png";

                var restored = run.RestoreOwnIconsWhereDownloadFailed(data, Undownloaded);

                CollectionAssert.AreEquivalent(new[] { "ACH_CURIOUS" }, restored.ToList());
            }

            Assert.AreEqual("cache/dragon.png", data.Achievements[0].UnlockedIconPath);
            Assert.AreEqual("steam_curious.png", data.Achievements[1].UnlockedIconPath);
            Assert.AreEqual("steam_curious_gray.png", data.Achievements[1].LockedIconPath);
            var gameId = data.PlayniteGameId.Value;
            CollectionAssert.AreEquivalent(new[] { "ACH_CURIOUS" }, overlay.Links.GetIconFallbacks(gameId).ToList());

            // Next refresh: the download reuses both files, so the fallen-back one is reset to the
            // borrowed icon and downloaded again; once it arrives nothing is left falling back.
            var next = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"), Row("ACH_CURIOUS", "Curiosity"));
            next.Game = data.Game;
            next.PlayniteGameId = data.PlayniteGameId;
            using (var run = overlay.BeginRun())
            {
                Assert.IsFalse(await run.ApplyAsync(next.Game, next, CancellationToken.None));
                next.Achievements[0].UnlockedIconPath = "cache/dragon.png";
                next.Achievements[1].UnlockedIconPath = "cache/curious.png";

                var retried = run.ResetEarlierIconFallbacks(next);

                CollectionAssert.AreEquivalent(new[] { "ACH_CURIOUS" }, retried.ToList());
                Assert.AreEqual("https://psn/curiosity.png", next.Achievements[1].UnlockedIconPath);
                next.Achievements[1].UnlockedIconPath = "cache/curious.png";
                Assert.AreEqual(0, run.RestoreOwnIconsWhereDownloadFailed(next, Undownloaded).Count);
            }

            Assert.AreEqual(0, overlay.Links.GetIconFallbacks(gameId).Count);
        }

        [TestMethod]
        public void MatchWithPairings_AppliesUserPairingsBeforeNames()
        {
            var renamed = Row("a", "Night Owl");
            var excluded = Row("b", "Collector");
            var byName = Row("c", "Dragon Slayer");
            var owl = Row("p1", "Nocturnal");
            var collector = Row("p2", "Collector");
            var dragon = Row("p3", "Dragon Slayer");
            var pairings = new Dictionary<string, string> { { "a", "p1" }, { "b", string.Empty } };

            var matches = MetadataOverlayRun.MatchWithPairings(
                new[] { renamed, excluded, byName },
                new[] { owl, collector, dragon },
                pairings,
                matchApiNames: false);

            Assert.AreSame(owl, matches.Single(match => match.Target == renamed).Source);
            Assert.AreSame(dragon, matches.Single(match => match.Target == byName).Source);
            Assert.IsFalse(matches.Any(match => match.Target == excluded));
        }

        [TestMethod]
        public async Task Pairings_StayWithTheirLink_AndDropWhenItChanges()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var gameId = data.PlayniteGameId.Value;
            Assert.IsTrue(overlay.Links.SetPairing(gameId, "PlayStation", "ACH_DRAGON", string.Empty));
            overlay.Links.SetUser(gameId, "PlayStation", "link-PlayStation");
            Assert.IsTrue(overlay.Links.TryGet(gameId, "PlayStation", out var kept));
            Assert.AreEqual(string.Empty, kept.Pairings["ACH_DRAGON"]);

            overlay.Links.SetUser(gameId, "PlayStation", "another-page");
            Assert.IsTrue(overlay.Links.TryGet(gameId, "PlayStation", out var changed));
            Assert.AreEqual(0, changed.Pairings.Count);
        }

        [TestMethod]
        public async Task NewPairing_ReportsAnIconChange_SoItsFileIsRewritten()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows =
                {
                    Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png"),
                    Row("p2", "Curiosity", icon: "https://psn/curiosity.png")
                }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"), Row("ACH_CURIOUS", "Curiousity"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var gameId = data.PlayniteGameId.Value;
            overlay.Links.SetPairing(gameId, "PlayStation", "ACH_CURIOUS", "p2");
            var next = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"), Row("ACH_CURIOUS", "Curiousity"));
            next.Game = data.Game;
            next.PlayniteGameId = data.PlayniteGameId;

            using (var run = overlay.BeginRun())
            {
                Assert.IsTrue(await run.ApplyAsync(next.Game, next, CancellationToken.None));
            }

            Assert.AreEqual("https://psn/curiosity.png", next.Achievements[1].UnlockedIconPath);
        }

        [TestMethod]
        public async Task CapstoneOnlySource_CountsItsPlatinum_AndKeepsNoMatchList()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons | MetadataField.CapstoneArt);
            var platinum = Row("p0", "Master", icon: "https://psn/platinum.png");
            platinum.TrophyType = "platinum";
            playstation.Rows.Add(platinum);
            playstation.Rows.Add(Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png"));
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"), Row("ACH_OTHER", "Other"));
            var overlay = Overlay(new[] { playstation }, Choices(capstoneArt: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var gameId = data.PlayniteGameId.Value;
            var coverage = overlay.Links.GetCoverage(gameId, "PlayStation");
            Assert.AreEqual(1, coverage.Matched);
            Assert.AreEqual(1, coverage.Total);
            Assert.IsNull(overlay.Links.Snapshots.Get(gameId, "PlayStation", "link-PlayStation"));
            Assert.AreEqual("Master", overlay.Links.GetCapstoneText(gameId).Title);
        }

        [TestMethod]
        public async Task IconSource_KeepsItsListAndMatches()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var snapshot = overlay.Links.Snapshots.Get(data.PlayniteGameId.Value, "PlayStation", "link-PlayStation");
            Assert.AreEqual(1, snapshot.Rows.Count);
            Assert.AreEqual("p1", snapshot.Matches["ACH_DRAGON"]);
        }

        [TestMethod]
        public async Task NativeSource_IsSkipped()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons) { NativeFor = "PSN" };
            playstation.Rows.Add(Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png"));
            var data = Game("PSN", Row("1", "Dragon Slayer", icon: "psn.png"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("psn.png", data.Achievements[0].UnlockedIconPath);
            Assert.AreEqual(0, playstation.ResolveCount);
        }

        [TestMethod]
        public async Task Categories_ReplaceProviderCategory_AndKeepItsTags()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories | MetadataField.Tags);
            hunters.Rows.Add(Row("A", "One", category: "Story", type: "DLC|Multiplayer"));
            var named = Row("e1", "One", category: "Chapter 1", type: "Missable");
            var data = Game("Epic", named);

            using (var run = Overlay(new[] { hunters }, Choices(categories: hunters.Key)).BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", named.Category);
            Assert.AreEqual("DLC", named.CategoryKind);
            Assert.AreEqual("Missable", named.Tags);
        }

        [TestMethod]
        public async Task Tags_ReplaceProviderTags_AndKeepItsCategory()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories | MetadataField.Tags);
            hunters.Rows.Add(Row("A", "One", category: "Story", type: "Multiplayer"));
            var named = Row("e1", "One", category: "Chapter 1", type: "Missable|Hardcore");
            var data = Game("Epic", named);

            using (var run = Overlay(new[] { hunters }, Choices(tags: hunters.Key)).BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Chapter 1", named.Category);
            // The source's tags replace the provider's; the unlock mode stays.
            CollectionAssert.AreEquivalent(
                new[] { "Multiplayer", "Hardcore" },
                PlayniteAchievements.Services.Achievements.AchievementTagHelper.Parse(named.Tags));
        }

        [TestMethod]
        public async Task CapstoneArt_IsRecordedFromThePlatinumRow()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.CapstoneArt);
            var platinum = Row("p0", "Master", icon: "https://psn/platinum.png");
            platinum.TrophyType = "platinum";
            playstation.Rows.Add(platinum);
            playstation.Rows.Add(Row("p1", "One", icon: "https://psn/one.png"));
            var data = Game("Xbox", Row("x1", "One"));
            var overlay = Overlay(new[] { playstation }, Choices(capstoneArt: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("https://psn/platinum.png", overlay.Links.GetCapstoneArtUrl(data.PlayniteGameId.Value));
        }

        [TestMethod]
        public async Task StoredNoMatch_StopsResolution()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons);
            var data = Game("Steam", Row("a", "One"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            overlay.Links.SetUser(data.PlayniteGameId.Value, "PlayStation", null);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, playstation.ResolveCount);
            Assert.AreEqual(0, playstation.FetchCount);
        }

        [TestMethod]
        public async Task SharedIds_MatchAcrossLanguages_AndCoverageIsRecorded()
        {
            // German platform titles, English Steam titles: only the reused IDs can pair them.
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { IdsMayBeShared = true };
            hunters.Rows.Add(Row("ACH_DRAGON", "Dragon Slayer", category: "Story"));
            hunters.Rows.Add(Row("ACH_COINS", "Collector", category: "Collectibles"));
            hunters.Rows.Add(Row("ACH_EXTRA", "Extra", category: "DLC"));
            var dragon = Row("ACH_DRAGON", "Drachentöter");
            var coins = Row("ACH_COINS", "Sammler");
            var unknown = Row("GOG_ONLY", "Nur auf GOG");
            var data = Game("GOG", dragon, coins, unknown);
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", dragon.Category);
            Assert.AreEqual("Collectibles", coins.Category);
            Assert.IsNull(unknown.Category);
            var coverage = overlay.Links.GetCoverage(data.PlayniteGameId.Value, "SteamHunters");
            Assert.AreEqual(2, coverage.Matched);
            Assert.AreEqual(3, coverage.Total);
        }

        [TestMethod]
        public async Task LowIdOverlap_FallsBackToTitles()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { IdsMayBeShared = true };
            hunters.Rows.Add(Row("ACH_ONE", "One", category: "Story"));
            hunters.Rows.Add(Row("ACH_TWO", "Two", category: "Side"));
            // One of three distinctive IDs coincides (33%), under the threshold; "ACH_TWO" names a
            // different achievement here, so an ID join would mislabel it.
            var one = Row("e_one", "One");
            var two = Row("ACH_TWO", "Something else");
            var three = Row("e_three", "Two");
            var data = Game("Epic", one, two, three);

            using (var run = Overlay(new[] { hunters }, Choices(categories: hunters.Key)).BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", one.Category);
            Assert.IsNull(two.Category);
            Assert.AreEqual("Side", three.Category);
        }

        [TestMethod]
        public async Task NameFoundLink_WithFewMatches_IsNeitherKeptNorUsed()
        {
            // "Thief: Definitive Edition" found as "Thief Gold": a different game's achievements.
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { FoundByName = true };
            hunters.Rows.Add(Row("A", "Keeper of Secrets", category: "Story"));
            var first = Row("g1", "Ghost");
            var second = Row("g2", "Opportunist");
            var data = Game("GOG", first, second);
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.IsNull(first.Category);
            Assert.IsFalse(overlay.Links.TryGet(data.PlayniteGameId.Value, "SteamHunters", out _));
        }

        [TestMethod]
        public async Task NameFoundLink_ThatMatches_IsKept()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { FoundByName = true };
            hunters.Rows.Add(Row("A", "Ghost", category: "Story"));
            hunters.Rows.Add(Row("B", "Opportunist", category: "Side"));
            var data = Game("GOG", Row("g1", "Ghost"), Row("g2", "Opportunist"));
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", data.Achievements[0].Category);
            Assert.IsTrue(overlay.Links.TryGet(data.PlayniteGameId.Value, "SteamHunters", out var link));
            Assert.AreEqual("link-SteamHunters", link.Id);
        }

        [TestMethod]
        public void LooseKey_ForgivesPunctuationAndEditions()
        {
            Assert.AreEqual(GameNameNormalizer.ToLooseKey("Deus Ex: Mankind Divided"), GameNameNormalizer.ToLooseKey("Deus Ex - Mankind Divided"));
            Assert.AreEqual("fallout new vegas", GameNameNormalizer.ToLooseKey("Fallout: New Vegas Ultimate Edition"));
            Assert.AreEqual("doom", GameNameNormalizer.ToLooseKey("DOOM (2016)"));
            Assert.AreEqual("marvel s spider man miles morales", GameNameNormalizer.ToLooseKey("Marvel’s Spider-Man™: Miles Morales"));
            Assert.AreEqual(string.Empty, GameNameNormalizer.ToLooseKey("ゼルダの伝説"));
        }

        [TestMethod]
        public async Task FailedFetch_KeepsTheStoredValues_NotTheProvidersOwn()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories | MetadataField.Tags) { FailFetch = true };
            var data = Game("Epic", Row("e1", "One", category: "Own", type: "Hardcore"), Row("e2", "Two"));
            var stored = Game("Epic", Row("e1", "One", category: "Story", type: "DLC|Missable"), Row("e2", "Two", category: "Side"));
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key, tags: hunters.Key), loadStored: _ => stored);
            overlay.Links.SetUser(data.PlayniteGameId.Value, hunters.Key, "link-SteamHunters");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", data.Achievements[0].Category);
            Assert.AreEqual("DLC", data.Achievements[0].CategoryKind);
            CollectionAssert.AreEquivalent(
                new[] { "Missable", "Hardcore" },
                PlayniteAchievements.Services.Achievements.AchievementTagHelper.Parse(data.Achievements[0].Tags),
                "the stored tags, with this refresh's unlock mode");
            Assert.AreEqual("Side", data.Achievements[1].Category);
        }

        [TestMethod]
        public async Task NoMatch_IsNotAFailure_AndKeepsNothing()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { FoundByName = true };
            hunters.Rows.Add(Row("A", "Unrelated", category: "Story"));
            var data = Game("Epic", Row("e1", "One"));
            var stored = Game("Epic", Row("e1", "One", category: "Old"));
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key), loadStored: _ => stored);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.IsNull(data.Achievements[0].Category);
        }

        [TestMethod]
        public async Task PlatformChoice_OverridesTheDefaults()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories);
            hunters.Rows.Add(Row("A", "One", category: "Story"));
            var settings = Choices();
            settings.MetadataPlatformChoices = new Dictionary<string, MetadataChoices>
            {
                ["Xbox"] = new MetadataChoices { Categories = "SteamHunters" }
            };
            var xbox = Game("Xbox", Row("x1", "One"));
            var epic = Game("Epic", Row("e1", "One"));

            using (var run = Overlay(new[] { hunters }, settings).BeginRun())
            {
                await run.ApplyAsync(xbox.Game, xbox, CancellationToken.None);
                await run.ApplyAsync(epic.Game, epic, CancellationToken.None);
            }

            Assert.AreEqual("Story", xbox.Achievements[0].Category);
            Assert.IsNull(epic.Achievements[0].Category);
        }

        private static PersistedSettings Choices(
            string icons = null,
            string categories = null,
            string tags = null,
            string capstoneArt = null,
            string trophyTypes = null,
            string points = null)
        {
            var settings = new PersistedSettings();
            settings.MetadataDefaults = new MetadataChoices
            {
                Icons = icons ?? string.Empty,
                Categories = categories ?? string.Empty,
                Tags = tags ?? string.Empty,
                Rarity = string.Empty,
                CapstoneArt = capstoneArt ?? string.Empty,
                TrophyTypes = trophyTypes ?? string.Empty,
                Points = points ?? string.Empty
            };
            return settings;
        }

        private MetadataOverlay Overlay(
            IReadOnlyList<IMetadataProvider> providers,
            PersistedSettings settings,
            CategoryArtDownload downloadCategoryArt = null,
            Func<Guid, GameAchievementData> loadStored = null)
        {
            return new MetadataOverlay(
                () => providers,
                () => settings,
                new MetadataLinkStore(_directory, new GameCustomDataStore(_directory), null),
                null,
                downloadCategoryArt,
                loadStored);
        }

        private static GameAchievementData Game(string providerKey, params AchievementDetail[] rows)
        {
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            return new GameAchievementData
            {
                ProviderKey = providerKey,
                PlayniteGameId = game.Id,
                Game = game,
                Achievements = new List<AchievementDetail>(rows)
            };
        }

        private static AchievementDetail Row(
            string apiName,
            string title,
            string icon = null,
            string locked = null,
            string category = null,
            string type = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = title,
                UnlockedIconPath = icon,
                LockedIconPath = locked,
                Category = category,
                CategoryKind = PlayniteAchievements.Services.Achievements.LegacyCategoryTypes.SplitKind(type),
                Tags = PlayniteAchievements.Services.Achievements.AchievementTagHelper.Normalize(type)
            };
        }

        private sealed class FakeSource : IMetadataProvider
        {
            public FakeSource(string key, MetadataField fields)
            {
                Key = key;
                SupportedFields = fields;
            }

            public string Key { get; }

            public string DisplayNameKey => Key;

            public string LinkPlaceholder => string.Empty;

            public MetadataField SupportedFields { get; }

            public bool IdsMayBeShared { get; set; }

            public bool FoundByName { get; set; }

            /// <summary>The fetch throws, as a source does when its site cannot be reached.</summary>
            public bool FailFetch { get; set; }

            public string NativeFor { get; set; }

            public List<AchievementDetail> Rows { get; } = new List<AchievementDetail>();

            public int BeginCount { get; private set; }

            public int EndCount { get; private set; }

            public int ResolveCount { get; private set; }

            public int FetchCount { get; private set; }

            public bool IsNativeFor(string servicingProviderKey) =>
                string.Equals(NativeFor, servicingProviderKey, StringComparison.OrdinalIgnoreCase);

            public Task<bool> BeginRunAsync(CancellationToken ct)
            {
                BeginCount++;
                return Task.FromResult(true);
            }

            public void EndRun() => EndCount++;

            public Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
            {
                ResolveCount++;
                return Task.FromResult(new MetadataSourceLink("link-" + Key, null, FoundByName));
            }

            public Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
            {
                FetchCount++;
                if (FailFetch)
                {
                    throw new InvalidOperationException("unreachable");
                }

                var set = new AchievementSourceSet(Key, linkId, Rows) { ApiNamesMayBeShared = IdsMayBeShared };
                foreach (var art in CategoryArt)
                {
                    set.CategoryArtUrls[art.Key] = art.Value;
                }

                return Task.FromResult(set);
            }

            public Dictionary<string, string> CategoryArt { get; } = new Dictionary<string, string>();
        }
    }
}
