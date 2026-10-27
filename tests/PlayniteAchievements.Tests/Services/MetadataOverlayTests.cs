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
        public async Task CollectionLists_GiveTheirGameArt_ToTheirCategories()
        {
            var source = new FakeSource("PlayStation", MetadataField.Categories);
            source.RowsByLink["me1"] = new List<AchievementDetail> { Row("a1", "Saren", category: "Mass Effect"), Row("a2", "Spectre", category: "Mass Effect") };
            source.RowsByLink["me2"] = new List<AchievementDetail> { Row("b1", "Suicide Mission", category: "Mass Effect 2") };
            source.GameArtByLink["me1"] = "https://art/me1.png";
            source.GameArtByLink["me2"] = "https://art/me2.png";
            var data = Game("Steam", Row("A", "Saren"), Row("B", "Spectre"), Row("C", "Suicide Mission"));
            var saved = new List<(string Label, string ImageUrl)>();
            var overlay = Overlay(
                new[] { source },
                Choices(categories: "PlayStation"),
                (gameId, art, ct) =>
                {
                    saved.AddRange(art);
                    return Task.CompletedTask;
                });
            overlay.Links.SetUser(data.PlayniteGameId.Value, "PlayStation", "me1, me2");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            CollectionAssert.AreEquivalent(
                new[] { ("Mass Effect", "https://art/me1.png"), ("Mass Effect 2", "https://art/me2.png") },
                saved);
        }

        [TestMethod]
        public async Task SingleList_GivesNoGameArt()
        {
            var source = new FakeSource("PlayStation", MetadataField.Categories)
            {
                Rows = { Row("a1", "Saren", category: "Mass Effect") }
            };
            source.GameArtByLink["me1"] = "https://art/me1.png";
            var data = Game("Steam", Row("A", "Saren"));
            var saved = new List<(string Label, string ImageUrl)>();
            var overlay = Overlay(
                new[] { source },
                Choices(categories: "PlayStation"),
                (gameId, art, ct) =>
                {
                    saved.AddRange(art);
                    return Task.CompletedTask;
                });
            overlay.Links.SetUser(data.PlayniteGameId.Value, "PlayStation", "me1");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, saved.Count);
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
        public async Task Text_ReplacesMatchedNamesAndDescriptions_AfterMatching()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Text | MetadataField.TrophyTypes);
            var slayer = Row("p1", "Dragon Slayer");
            slayer.Description = "Defeat the dragon.";
            slayer.TrophyType = "gold";
            var hidden = Row("p2", "Secret Ending");
            playstation.Rows.Add(slayer);
            playstation.Rows.Add(hidden);
            var dragon = Row("ACH_DRAGON", "DRAGON SLAYER");
            dragon.Description = "kill dragon";
            var secret = Row("ACH_SECRET", "Secret ending");
            secret.Description = "Own description";
            var unmatched = Row("ACH_OTHER", "Something Else");
            var data = Game("Steam", dragon, secret, unmatched);
            var overlay = Overlay(new IMetadataProvider[] { playstation }, Choices(trophyTypes: "PlayStation", text: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Dragon Slayer", dragon.DisplayName);
            Assert.AreEqual("Defeat the dragon.", dragon.Description);
            Assert.AreEqual("gold", dragon.TrophyType, "other fields still match on the platform's own title");
            Assert.AreEqual("Secret Ending", secret.DisplayName);
            Assert.AreEqual("Own description", secret.Description, "a source without a description leaves the game's own");
            Assert.AreEqual("Something Else", unmatched.DisplayName);
        }

        [TestMethod]
        public async Task Text_InAnotherLanguage_LeavesTheGamesOwnText()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Text | MetadataField.TrophyTypes) { TextInPluginLanguage = false };
            var slayer = Row("p1", "Dragon Slayer");
            slayer.TrophyType = "gold";
            playstation.Rows.Add(slayer);
            var dragon = Row("ACH_DRAGON", "DRAGON SLAYER");
            var data = Game("Steam", dragon);
            var overlay = Overlay(new IMetadataProvider[] { playstation }, Choices(trophyTypes: "PlayStation", text: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("DRAGON SLAYER", dragon.DisplayName);
            Assert.AreEqual("gold", dragon.TrophyType, "the source's other fields still apply");
        }

        [TestMethod]
        public async Task Text_FromAFailedSource_KeepsTheStoredText()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Text) { FailFetch = true };
            var data = Game("Steam", Row("ACH_DRAGON", "DRAGON SLAYER"));
            var storedRow = Row("ACH_DRAGON", "Dragon Slayer");
            storedRow.Description = "Defeat the dragon.";
            var stored = Game("Steam", storedRow);
            var overlay = Overlay(new IMetadataProvider[] { playstation }, Choices(text: "PlayStation"), loadStored: _ => stored);
            overlay.Links.SetUser(data.PlayniteGameId.Value, playstation.Key, "link-PlayStation");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Dragon Slayer", data.Achievements[0].DisplayName);
            Assert.AreEqual("Defeat the dragon.", data.Achievements[0].Description);
        }

        [TestMethod]
        public void Text_IsOffByDefault_AndForPlatformsNoSourceHas()
        {
            var settings = new PersistedSettings
            {
                MetadataDefaults = new MetadataChoices { Icons = string.Empty }
            };

            Assert.AreEqual(string.Empty, settings.MetadataDefaults.Text, "a configuration saved before Text existed completes to Off");
            Assert.AreEqual(string.Empty, settings.ResolveMetadataChoices("Steam").Text);
            Assert.AreEqual("PlayStation", settings.ResolveMetadataChoices("Steam", new MetadataChoices { Text = "PlayStation" }).Text);
            Assert.AreEqual(string.Empty, settings.ResolveMetadataChoices("Riot", new MetadataChoices { Text = "PlayStation" }).Text);
            Assert.IsFalse(new MetadataChoices { Text = string.Empty }.IsEmpty());
            Assert.IsFalse(new MetadataChoices { Text = "Xbox" }.SameAs(new MetadataChoices()));
            Assert.AreEqual("Xbox", new MetadataChoices { Text = "Xbox" }.Clone().Text);
        }

        [TestMethod]
        public void PointsServedByTheExophasePage_WhenTextLoadsIt()
        {
            var providers = new IMetadataProvider[]
            {
                new FakeSource("Xbox", MetadataField.Points | MetadataField.Text),
                new FakeSource("TrueGaming", MetadataField.Tags | MetadataField.Points | MetadataField.TrophyTypes)
            };

            var routed = MetadataSourceRouting.Route(new MetadataChoices { Points = "Xbox", Text = "Xbox" }, providers, "Steam");

            Assert.AreEqual("Xbox", routed.Points);
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
        public void PlatformsNoSourceHas_TakeNothing_WhateverIsStored()
        {
            var stored = new MetadataChoices
            {
                Icons = MetadataChoices.Exophase,
                Categories = MetadataChoices.SteamHunters,
                Tags = MetadataChoices.TrueGaming,
                Rarity = MetadataChoices.Exophase,
                CapstoneArt = MetadataChoices.PlayStation,
                TrophyTypes = MetadataChoices.PlayStation,
                Points = MetadataChoices.Xbox
            };
            var settings = new PersistedSettings
            {
                MetadataPlatformChoices = new Dictionary<string, MetadataChoices> { ["Riot"] = stored.Clone() }
            };

            var riot = settings.ResolveMetadataChoices("Riot", stored);
            var steam = settings.ResolveMetadataChoices("Steam");

            Assert.IsTrue(riot.SameAs(new MetadataChoices
            {
                Icons = string.Empty,
                Categories = string.Empty,
                Tags = string.Empty,
                Rarity = string.Empty,
                CapstoneArt = string.Empty,
                TrophyTypes = string.Empty,
                Points = string.Empty,
                Text = string.Empty
            }));
            Assert.AreEqual(MetadataChoices.TrueGaming, steam.Tags);
            Assert.AreEqual(MetadataChoices.PlayStation, steam.CapstoneArt);
            Assert.IsTrue(MetadataChoices.TakesSources(null));
            Assert.IsTrue(MetadataChoices.TakesSources("Meta"));
            Assert.IsFalse(MetadataChoices.TakesSources("hoyoverse"));
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
        public void MatchWithPairings_FindsARowByAnotherIdItWentBy()
        {
            var grilled = Row("battle_trinity_0-1", "Grilled Tasty");
            var joined = Row("a1", "Grilled Tastes Better");
            joined.Points = 15;
            var other = Row("a2", "Crabby Patty");
            var aliases = new Dictionary<string, AchievementDetail>(System.StringComparer.OrdinalIgnoreCase) { { "s1", joined } };

            var matches = MetadataOverlayRun.MatchWithPairings(
                new[] { grilled },
                new[] { joined, other },
                new Dictionary<string, string> { { "battle_trinity_0-1", "s1" } },
                matchApiNames: false,
                aliases: aliases);

            Assert.AreSame(joined, matches.Single(match => match.Target == grilled).Source, "a pairing to a joined listing's id finds the row");
        }

        [TestMethod]
        public async Task PairedPlatinum_TakesItsRankInTheSameRefreshAsItsText()
        {
            var platinum = Row("p1", "Platinum Trophy", icon: "https://psn/platinum.png");
            platinum.Description = "Obtain all trophies";
            platinum.TrophyType = "platinum";
            var playstation = new FakeSource("PlayStation", MetadataField.Text | MetadataField.TrophyTypes | MetadataField.CapstoneArt)
            {
                Rows = { platinum }
            };
            var completionist = Row("ACH_ALL", "Completionist");
            completionist.Description = "All achievements acquired";
            var data = Game("Steam", completionist);
            var overlay = Overlay(new[] { playstation }, Choices(capstoneArt: "PlayStation", trophyTypes: "PlayStation", text: "PlayStation"));
            var gameId = data.PlayniteGameId.Value;
            overlay.Links.SetUser(gameId, "PlayStation", "link-PlayStation");
            Assert.IsTrue(overlay.Links.SetPairing(gameId, "PlayStation", "ACH_ALL", "p1"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Platinum Trophy", completionist.DisplayName);
            Assert.AreEqual("platinum", completionist.TrophyType);
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
        public async Task AchievementOneSourceMatched_IsMatchedByAnotherUnderTheFirstSourcesTitle()
        {
            // Exophase pairs the achievement by its id and knows it as "Defeat Saren", the title
            // TrueGaming lists it under; the game's own title matches neither.
            var exophase = new FakeSource("Exophase", MetadataField.Icons)
            {
                IdsMayBeShared = true,
                Rows = { Row("ACH_SAREN_DOWN", "Defeat Saren", icon: "https://exo/saren.png") }
            };
            var trueGaming = new FakeSource("TrueGaming", MetadataField.Categories)
            {
                Rows = { Row("t1", "Defeat Saren", category: "Mass Effect") }
            };
            var data = Game("Steam", Row("ACH_SAREN_DOWN", "Saren Defeated"));
            var overlay = Overlay(new IMetadataProvider[] { exophase, trueGaming }, Choices(icons: "Exophase", categories: "TrueGaming"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("https://exo/saren.png", data.Achievements[0].UnlockedIconPath);
            Assert.AreEqual("Mass Effect", data.Achievements[0].Category);
        }

        [TestMethod]
        public async Task SourceThatFindsNothing_SearchesAgainUnderNamesAnotherFound()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") },
                ResolvedLabel = "Dragon Game Remastered\nDragon Game\tDragon Game Classic"
            };
            var steam = new FakeSource("SteamHunters", MetadataField.Categories)
            {
                Rows = { Row("s1", "Dragon Slayer", category: "Dragon Game") },
                ResolveWhen = request => request.AlternateNames.Contains("Dragon Game Remastered") &&
                                         request.CategoryNames.TryGetValue("Dragon Game", out var names) && names.Contains("Dragon Game Classic")
            };
            var data = Game("Xbox", Row("ACH_DRAGON", "Dragon Slayer"));
            var overlay = Overlay(new IMetadataProvider[] { playstation, steam }, Choices(icons: "PlayStation", categories: "SteamHunters"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(2, steam.ResolveCount);
            Assert.IsTrue(overlay.Links.TryGet(data.PlayniteGameId.Value, "SteamHunters", out var link));
            Assert.AreEqual("link-SteamHunters", link.Id);
            Assert.AreEqual("Dragon Game", data.Achievements[0].Category);
        }

        [TestMethod]
        public async Task SourceThatFindsNothing_DoesNotSearchAgainWithoutNewNames()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var steam = new FakeSource("SteamHunters", MetadataField.Categories) { ResolveWhen = request => false };
            var data = Game("Xbox", Row("ACH_DRAGON", "Dragon Slayer"));
            var overlay = Overlay(new IMetadataProvider[] { playstation, steam }, Choices(icons: "PlayStation", categories: "SteamHunters"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(1, steam.ResolveCount);
        }

        [TestMethod]
        public async Task Pairings_StayWhenACollectionListIsAdded()
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
            overlay.Links.SetUser(gameId, "PlayStation", "second-game, link-PlayStation");

            Assert.IsTrue(overlay.Links.TryGet(gameId, "PlayStation", out var kept));
            Assert.AreEqual(string.Empty, kept.Pairings["ACH_DRAGON"]);
        }

        [TestMethod]
        public async Task CollectionLink_FetchesEachList_AndMatchesAcrossThem()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons);
            playstation.RowsByLink["mass-effect"] = new List<AchievementDetail> { Row("me1", "Saren", icon: "https://psn/me1.png") };
            playstation.RowsByLink["mass-effect-2"] = new List<AchievementDetail> { Row("me2", "Suicide Mission", icon: "https://psn/me2.png") };
            var data = Game("Steam", Row("ACH_SAREN", "Saren"), Row("ACH_SUICIDE", "Suicide Mission"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            overlay.Links.SetUser(data.PlayniteGameId.Value, "PlayStation", "mass-effect,mass-effect-2");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            CollectionAssert.AreEqual(new[] { "mass-effect", "mass-effect-2" }, playstation.FetchedIds);
            Assert.AreEqual("https://psn/me1.png", data.Achievements[0].UnlockedIconPath);
            Assert.AreEqual("https://psn/me2.png", data.Achievements[1].UnlockedIconPath);
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
        public async Task CollectionLink_FilesEachListsPlatinumUnderItsGame_AndNoneForTheWholeGame()
        {
            AchievementDetail Platinum(string apiName, string icon, string description)
            {
                var row = Row(apiName, "N7 Elite", icon: icon);
                row.TrophyType = "platinum";
                row.Description = description;
                return row;
            }

            var playstation = new FakeSource("PlayStation", MetadataField.CapstoneArt | MetadataField.Icons);
            playstation.RowsByLink["me1"] = new List<AchievementDetail> { Platinum("p1", "https://psn/me1.png", "All of Mass Effect."), Row("a1", "Saren"), Row("a2", "Spectre") };
            playstation.RowsByLink["me2"] = new List<AchievementDetail> { Platinum("p2", "https://psn/me2.png", "All of Mass Effect 2."), Row("b1", "Suicide Mission"), Row("b2", "Collector") };
            var finished = Row("ME2_ALL", "Completionist", category: "Mass Effect 2");
            finished.Description = "All of Mass Effect 2.";
            var data = Game(
                "Steam",
                Row("ME1_SAREN", "Saren", category: "Mass Effect 1"),
                Row("ME1_SPECTRE", "Spectre", category: "Mass Effect 1"),
                Row("ME2_SUICIDE", "Suicide Mission", category: "Mass Effect 2"),
                Row("ME2_COLLECTOR", "Collector", category: "Mass Effect 2"),
                finished);
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation", capstoneArt: "PlayStation"));
            overlay.Links.SetUser(data.PlayniteGameId.Value, "PlayStation", "me1, me2");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var gameId = data.PlayniteGameId.Value;
            var capstones = overlay.Links.GetCategoryCapstones(gameId);
            CollectionAssert.AreEqual(new[] { "Mass Effect 1", "Mass Effect 2" }, capstones.Select(capstone => capstone.Category).ToList());
            Assert.AreEqual("https://psn/me1.png", capstones[0].ArtUrl);
            Assert.IsNull(capstones[0].MatchApiName);
            Assert.AreEqual("ME2_ALL", capstones[1].MatchApiName);
            Assert.IsNull(overlay.Links.GetCapstoneArtUrl(gameId), "a collection has no whole-game platinum");
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
        public async Task CapstoneArt_IsThePlatinumsSavedIcon_WhenTheSourcesIconsWereSaved()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons | MetadataField.CapstoneArt);
            var platinum = Row("p0", "Master", icon: "https://psn/platinum.png");
            platinum.TrophyType = "platinum";
            playstation.Rows.Add(platinum);
            playstation.Rows.Add(Row("p1", "One", icon: "https://psn/one.png"));
            var data = Game("Xbox", Row("x1", "One"));
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation", capstoneArt: "PlayStation"), iconSaver: new FakeIconSaver());

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(FakeIconSaver.PathOf("PlayStation", "p0"), overlay.Links.GetCapstoneArtUrl(data.PlayniteGameId.Value));
        }

        [TestMethod]
        public async Task CapstoneArtPlatinum_MatchesTheGamesAchievementByTitle_AndLeavesItsTrophyType()
        {
            var data = Game("Steam", Row("ACH_MASTER", "Master"), Row("ACH_ONE", "One"));
            var overlay = await ApplyPlatinumAsync(data, "Master", "Earn every trophy.");

            Assert.AreEqual("ACH_MASTER", overlay.Links.GetCapstoneMatch(data.PlayniteGameId.Value));
            // Labeling it platinum is the auto capstone's, in custom data; a platinum type here
            // would seed a capstone in the cache whatever the setting.
            Assert.IsNull(data.Achievements[0].TrophyType);
        }

        [TestMethod]
        public async Task CapstoneArtPlatinum_FallsBackToTheDescription()
        {
            var all = Row("ACH_ALL", "Completionist");
            all.Description = "Earn every trophy.";
            var data = Game("Steam", all, Row("ACH_ONE", "One"));
            var overlay = await ApplyPlatinumAsync(data, "Master", "Earn every trophy.");

            Assert.AreEqual("ACH_ALL", overlay.Links.GetCapstoneMatch(data.PlayniteGameId.Value));
        }

        [TestMethod]
        public async Task CapstoneArtPlatinum_MatchesNoDlcAchievement()
        {
            var data = Game("Steam", Row("ACH_MASTER", "Master", type: "DLC"), Row("ACH_ONE", "One"));
            var overlay = await ApplyPlatinumAsync(data, "Master", null);

            Assert.IsNull(overlay.Links.GetCapstoneMatch(data.PlayniteGameId.Value));
            Assert.IsNull(data.Achievements[0].TrophyType);
        }

        [TestMethod]
        public async Task CapstoneArtPlatinum_SharedDescription_MatchesNothing()
        {
            var first = Row("ACH_A", "A");
            first.Description = "Earn every trophy.";
            var second = Row("ACH_B", "B");
            second.Description = "Earn every trophy.";
            var data = Game("Steam", first, second, Row("ACH_ONE", "One"));
            var overlay = await ApplyPlatinumAsync(data, "Master", "Earn every trophy.");

            Assert.IsNull(overlay.Links.GetCapstoneMatch(data.PlayniteGameId.Value));
        }

        /// <summary>Overlays a PlayStation source used for capstone art: a platinum and a trophy titled "One".</summary>
        private async Task<MetadataOverlay> ApplyPlatinumAsync(GameAchievementData data, string title, string description)
        {
            var playstation = new FakeSource("PlayStation", MetadataField.CapstoneArt);
            var platinum = Row("p0", title, icon: "https://psn/platinum.png");
            platinum.TrophyType = "platinum";
            platinum.Description = description;
            playstation.Rows.Add(platinum);
            playstation.Rows.Add(Row("p1", "One", icon: "https://psn/one.png"));
            var overlay = Overlay(new[] { playstation }, Choices(capstoneArt: "PlayStation"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            return overlay;
        }

        [TestMethod]
        public async Task ChangedCategories_LookAgain_AndABetterLinkReplacesTheOld()
        {
            var playstation = RelinkSource();
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            await Refresh(playstation, game, Row("e1", "One"), Row("e2", "Two"));
            Assert.IsTrue(_lastOverlay.Links.TryGet(game.Id, "PlayStation", out var first));
            Assert.AreEqual("old", first.Id);

            playstation.ResolvedId = "new";
            playstation.ResolvedMatched = 2;
            await Refresh(playstation, game, Row("e1", "One", category: "Story"), Row("e2", "Two"));
            Assert.IsTrue(_lastOverlay.Links.TryGet(game.Id, "PlayStation", out var second));
            Assert.AreEqual("new", second.Id);
            Assert.AreEqual(2, playstation.ResolveCount);

            // The new inputs are recorded: the next refresh does not look again.
            await Refresh(playstation, game, Row("e1", "One", category: "Story"), Row("e2", "Two"));
            Assert.AreEqual(2, playstation.ResolveCount);
        }

        [TestMethod]
        public async Task ChangedCategories_KeepTheLink_WhenTheNewLookupIsNoBetter()
        {
            var playstation = RelinkSource();
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            await Refresh(playstation, game, Row("e1", "One"), Row("e2", "Two"));

            playstation.ResolvedId = "new";
            playstation.ResolvedMatched = 1;
            await Refresh(playstation, game, Row("e1", "One", category: "Story"), Row("e2", "Two"));
            await Refresh(playstation, game, Row("e1", "One", category: "Story"), Row("e2", "Two"));

            Assert.IsTrue(_lastOverlay.Links.TryGet(game.Id, "PlayStation", out var link));
            Assert.AreEqual("old", link.Id);
            Assert.AreEqual(2, playstation.ResolveCount, "looked again once, then not");
        }

        [TestMethod]
        public async Task StoredNoMatch_IsLookedForAgain_WhenCategoriesChange()
        {
            var playstation = RelinkSource();
            playstation.ResolveWhen = request => request.Data.Achievements.Any(row => row.Category == "Story");
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            await Refresh(playstation, game, Row("e1", "One"));
            Assert.IsTrue(_lastOverlay.Links.TryGet(game.Id, "PlayStation", out var none));
            Assert.IsTrue(none.IsNoMatch);

            await Refresh(playstation, game, Row("e1", "One", category: "Story"));
            Assert.IsTrue(_lastOverlay.Links.TryGet(game.Id, "PlayStation", out var found));
            Assert.AreEqual("old", found.Id);
        }

        [TestMethod]
        public async Task UserLink_IsNeverLookedForAgain()
        {
            var playstation = RelinkSource();
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            _lastOverlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            _lastOverlay.Links.SetUser(game.Id, "PlayStation", "old");
            await Refresh(playstation, game, Row("e1", "One"));
            await Refresh(playstation, game, Row("e1", "One", category: "Story"));

            Assert.AreEqual(0, playstation.ResolveCount);
        }

        [TestMethod]
        public async Task LinkWithoutABasis_TakesTheCurrentOne_WithoutASearch()
        {
            var playstation = RelinkSource();
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            _lastOverlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"));
            _lastOverlay.Links.SetAuto(game.Id, "PlayStation", "old");
            await Refresh(playstation, game, Row("e1", "One"));
            Assert.AreEqual(0, playstation.ResolveCount);

            await Refresh(playstation, game, Row("e1", "One", category: "Story"));
            Assert.AreEqual(1, playstation.ResolveCount);
        }

        private MetadataOverlay _lastOverlay;

        /// <summary>A PlayStation icon source whose lookup finds "old", a list matching only "One".</summary>
        private static FakeSource RelinkSource()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons) { ResolvedId = "old" };
            playstation.RowsByLink["old"] = new List<AchievementDetail> { Row("p1", "One", icon: "https://psn/one.png") };
            playstation.RowsByLink["new"] = new List<AchievementDetail>
            {
                Row("p1", "One", icon: "https://psn/one.png"),
                Row("p2", "Two", icon: "https://psn/two.png")
            };
            return playstation;
        }

        /// <summary>
        /// One refresh of <paramref name="game"/> with fresh rows, as a provider scan hands them
        /// over, on the test's one overlay (made on first use).
        /// </summary>
        private async Task Refresh(FakeSource source, Game game, params AchievementDetail[] rows)
        {
            if (_lastOverlay == null)
            {
                _lastOverlay = Overlay(new[] { source }, Choices(icons: "PlayStation"));
            }

            var data = new GameAchievementData
            {
                ProviderKey = "Steam",
                PlayniteGameId = game.Id,
                Game = game,
                Achievements = new List<AchievementDetail>(rows)
            };
            using (var run = _lastOverlay.BeginRun())
            {
                await run.ApplyAsync(game, data, CancellationToken.None);
            }
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
        public async Task NameFoundLink_WithFewMatches_IsNotUsed_AndIsRememberedAsNoMatch()
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
            Assert.IsTrue(overlay.Links.TryGet(data.PlayniteGameId.Value, "SteamHunters", out var link));
            Assert.IsTrue(link.IsNoMatch);
            Assert.AreEqual(MetadataLinkOrigin.Auto, link.Origin);

            // The next refresh does not search again.
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(1, hunters.ResolveCount);

            // Reset lets matching look again.
            overlay.Links.Clear(data.PlayniteGameId.Value, "SteamHunters");
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(2, hunters.ResolveCount);
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
        public async Task UnreadableLookup_KeepsTheStoredValues_AndIsNotRememberedAsNoMatch()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { UnreadableLookup = true };
            var data = Game("Epic", Row("e1", "One"));
            var stored = Game("Epic", Row("e1", "One", category: "Story"));
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key), loadStored: _ => stored);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", data.Achievements[0].Category);
            Assert.IsFalse(overlay.Links.TryGet(data.PlayniteGameId.Value, hunters.Key, out _), "nothing is stored");

            // The next refresh looks again.
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(2, hunters.ResolveCount);
        }

        [TestMethod]
        public async Task UnavailableTags_KeepTheStoredTags_WhileCategoriesApply()
        {
            var trueGaming = new FakeSource("TrueGaming", MetadataField.Categories | MetadataField.Tags) { Unavailable = MetadataField.Tags };
            trueGaming.Rows.Add(Row("t1", "One", category: "Pack"));
            var data = Game("Steam", Row("s1", "One", category: "Own"));
            var stored = Game("Steam", Row("s1", "One", category: "Old", type: "Missable"));
            var overlay = Overlay(new[] { trueGaming }, Choices(categories: trueGaming.Key, tags: trueGaming.Key), loadStored: _ => stored);
            overlay.Links.SetUser(data.PlayniteGameId.Value, trueGaming.Key, "link-TrueGaming");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Pack", data.Achievements[0].Category, "the page's categories still apply");
            Assert.AreEqual("Missable", data.Achievements[0].Tags, "tags the page could not read stay as stored");
        }

        [TestMethod]
        public async Task ExtendedLink_ReplacesAnAutoLinkWithItsPairings_ButNotAUsersLink()
        {
            var trueGaming = new FakeSource("TrueGaming", MetadataField.Categories | MetadataField.Tags) { ExtendedId = "X, tt:X" };
            trueGaming.Rows.Add(Row("t1", "One", category: "Pack"));
            var data = Game("Steam", Row("s1", "One"));
            var gameId = data.PlayniteGameId.Value;
            var overlay = Overlay(new[] { trueGaming }, Choices(categories: trueGaming.Key));
            overlay.Links.SetAuto(gameId, trueGaming.Key, "X");
            overlay.Links.SetPairing(gameId, trueGaming.Key, "s1", "t1");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(MetadataField.Categories | MetadataField.CategoryArt, trueGaming.LastWanted, "the source is told what it is used for");
            Assert.IsTrue(overlay.Links.TryGet(gameId, trueGaming.Key, out var extended));
            Assert.AreEqual("X, tt:X", extended.Id);
            Assert.AreEqual("t1", extended.Pairings["s1"], "pairings stay with the extended link");
            Assert.IsNotNull(overlay.Links.Snapshots?.Get(gameId, trueGaming.Key, "X, tt:X"), "the match list is kept under the link the game holds");

            overlay.Links.SetUser(gameId, trueGaming.Key, "X");
            var next = Game("Steam", Row("s1", "One"));
            next.PlayniteGameId = gameId;
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, next, CancellationToken.None);
            }

            Assert.IsTrue(overlay.Links.TryGet(gameId, trueGaming.Key, out var user));
            Assert.AreEqual("X", user.Id, "a user's link is kept as set");
        }

        [TestMethod]
        public void PointsAndRanks_AreServedByTheSourceWhosePageLoadsAnyway()
        {
            var providers = new IMetadataProvider[]
            {
                new FakeSource("Xbox", MetadataField.Points | MetadataField.Icons),
                new FakeSource("PlayStation", MetadataField.TrophyTypes | MetadataField.CapstoneArt),
                new FakeSource("TrueGaming", MetadataField.Tags | MetadataField.Points | MetadataField.TrophyTypes)
            };

            var routed = MetadataSourceRouting.Route(
                new MetadataChoices { Tags = "TrueGaming", Points = "Xbox", TrophyTypes = "PlayStation", CapstoneArt = "PlayStation" },
                providers,
                "Steam");
            Assert.AreEqual("TrueGaming", routed.Points, "no other field loads the Xbox page");
            Assert.AreEqual("PlayStation", routed.TrophyTypes, "the PlayStation page loads for capstone art");

            var withIcons = MetadataSourceRouting.Route(new MetadataChoices { Icons = "Xbox", Points = "Xbox" }, providers, "Steam");
            Assert.AreEqual("Xbox", withIcons.Points, "the Xbox page loads for icons");

            Assert.IsFalse(MetadataSourceRouting.IsChoice(providers[2], MetadataField.Points));
            Assert.IsTrue(MetadataSourceRouting.IsChoice(providers[2], MetadataField.Tags));
        }

        [TestMethod]
        public async Task RoutedPoints_ComeFromTrueGaming_WithoutLoadingTheXboxPage()
        {
            var xbox = new FakeSource("Xbox", MetadataField.Points);
            var xboxRow = Row("x1", "One");
            xboxRow.Points = 10;
            xbox.Rows.Add(xboxRow);
            var trueGaming = new FakeSource("TrueGaming", MetadataField.Tags | MetadataField.Points | MetadataField.TrophyTypes);
            var trueRow = Row("t1", "One");
            trueRow.Points = 20;
            trueGaming.Rows.Add(trueRow);
            var data = Game("Steam", Row("s1", "One"));
            var overlay = Overlay(new IMetadataProvider[] { xbox, trueGaming }, Choices(tags: trueGaming.Key, points: xbox.Key));
            overlay.Links.SetUser(data.PlayniteGameId.Value, trueGaming.Key, "link-TrueGaming");
            overlay.Links.SetUser(data.PlayniteGameId.Value, xbox.Key, "link-Xbox");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(20, data.Achievements[0].Points);
            Assert.AreEqual(0, xbox.FetchCount, "the Xbox page is not loaded");
            Assert.AreEqual(MetadataField.Tags | MetadataField.Points, trueGaming.LastWanted);
        }

        [TestMethod]
        public async Task Rarity_TakesTheSourcesShare_OnPlatformsThatUseIt()
        {
            var exophase = new FakeSource("Exophase", MetadataField.Rarity);
            var rare = Row("x1", "One");
            rare.GlobalPercentUnlocked = 3.5;
            exophase.Rows.Add(rare);
            exophase.Rows.Add(Row("x2", "Two"));
            var settings = Choices();
            settings.MetadataDefaults.Rarity = "Exophase";
            var xbox = Game("Xbox", Row("a1", "One"), Row("a2", "Two"));
            xbox.Achievements[1].Rarity = RarityTier.Rare;
            var epic = Game("Epic", Row("e1", "One"));

            using (var run = Overlay(new[] { exophase }, settings).BeginRun())
            {
                await run.ApplyAsync(xbox.Game, xbox, CancellationToken.None);
                await run.ApplyAsync(epic.Game, epic, CancellationToken.None);
            }

            Assert.AreEqual(3.5, xbox.Achievements[0].GlobalPercentUnlocked);
            Assert.AreEqual(PercentRarityHelper.GetRarityTier(3.5), xbox.Achievements[0].Rarity);
            Assert.AreEqual(RarityTier.Rare, xbox.Achievements[1].Rarity, "no share keeps the platform's tier");
            Assert.IsNull(epic.Achievements[0].GlobalPercentUnlocked, "Epic does not take Exophase rarity");
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
            string points = null,
            string text = null)
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
                Points = points ?? string.Empty,
                Text = text ?? string.Empty
            };
            return settings;
        }

        private MetadataOverlay Overlay(
            IReadOnlyList<IMetadataProvider> providers,
            PersistedSettings settings,
            CategoryArtDownload downloadCategoryArt = null,
            Func<Guid, GameAchievementData> loadStored = null,
            FakeIconSaver iconSaver = null,
            EnglishTitleFetch englishTitles = null)
        {
            return new MetadataOverlay(
                () => providers,
                () => settings,
                MetadataTestStores.Links(_directory, new FakeMetadataStateDatabase()),
                null,
                downloadCategoryArt,
                loadStored,
                iconSaver == null ? null : new SourceIconDownload(iconSaver.SaveAsync),
                iconSaver == null ? null : new Action<Guid, string>(iconSaver.Delete),
                englishTitles,
                iconSaver == null ? null : new Action<IEnumerable<string>>(iconSaver.Sweep));
        }

        /// <summary>Saves each row's icon as C:\saved\&lt;source&gt;\&lt;ApiName&gt;.png, except the ones it is told fail.</summary>
        private sealed class FakeIconSaver
        {
            public HashSet<string> Failing { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public List<(string SourceKey, bool Force)> Saves { get; } = new List<(string SourceKey, bool Force)>();

            public List<string> Deleted { get; } = new List<string>();

            public static string PathOf(string sourceKey, string apiName) => $@"C:\saved\{sourceKey}\{apiName}.png";

            public Task<IReadOnlyDictionary<AchievementDetail, (string Unlocked, string Locked)>> SaveAsync(
                Guid gameId,
                string sourceKey,
                IReadOnlyList<MetadataSourceList> lists,
                bool force,
                CancellationToken ct)
            {
                var rows = lists.SelectMany(list => list.Rows);
                lock (Saves)
                {
                    Saves.Add((sourceKey, force));
                }

                IReadOnlyDictionary<AchievementDetail, (string Unlocked, string Locked)> saved = rows.ToDictionary(
                    row => row,
                    row => Failing.Contains(row.ApiName)
                        ? (row.UnlockedIconPath, row.LockedIconPath)
                        : (PathOf(sourceKey, row.ApiName), PathOf(sourceKey, row.ApiName)));
                return Task.FromResult(saved);
            }

            public void Delete(Guid gameId, string sourceKey) => Deleted.Add(sourceKey);

            /// <summary>The icons each sweep was told are in use, one entry per sweep.</summary>
            public List<List<string>> Sweeps { get; } = new List<List<string>>();

            public void Sweep(IEnumerable<string> usedIcons) => Sweeps.Add(usedIcons.ToList());
        }

        [TestMethod]
        public async Task UnusedSourceLists_AreSweptWhenTheLastOpenRunEnds_KeepingTheSnapshotsIcons()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            var saver = new FakeIconSaver();
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"), iconSaver: saver);

            using (overlay.BeginRun())
            {
                using (var run = overlay.BeginRun())
                {
                    await run.ApplyAsync(data.Game, data, CancellationToken.None);
                }

                Assert.AreEqual(0, saver.Sweeps.Count, "another run is still open and may still save files");
            }

            Assert.AreEqual(1, saver.Sweeps.Count);
            CollectionAssert.AreEqual(new[] { FakeIconSaver.PathOf("PlayStation", "p1") }, saver.Sweeps[0]);

            using (overlay.BeginRun(keepStored: true))
            {
            }

            Assert.AreEqual(1, saver.Sweeps.Count, "a run that asks no source saves nothing to sweep after");
        }

        [TestMethod]
        public async Task SourceIcons_AreSavedLocally_AndBorrowedFromTheSavedFiles()
        {
            var source = Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png", locked: "https://psn/dragon.png");
            var playstation = new FakeSource("PlayStation", MetadataField.Icons) { Rows = { source } };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            var saver = new FakeIconSaver();
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"), iconSaver: saver);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var saved = FakeIconSaver.PathOf("PlayStation", "p1");
            Assert.AreEqual(saved, data.Achievements[0].UnlockedIconPath, "the borrowed icon is the saved file");
            Assert.AreEqual(saved, data.Achievements[0].LockedIconPath);
            var snapshot = overlay.Links.Snapshots.Get(data.PlayniteGameId.Value, "PlayStation", "link-PlayStation");
            Assert.AreEqual(saved, snapshot.Rows.Single().Icon);
            Assert.AreEqual("https://psn/dragon.png", source.UnlockedIconPath, "the source's own row is left as it was");
            CollectionAssert.AreEqual(new[] { ("PlayStation", false) }, saver.Saves);
        }

        [TestMethod]
        public async Task SourceIconThatWasNotSaved_KeepsItsAddressForTheGame_AndShowsNoneInTheMatchList()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            var saver = new FakeIconSaver { Failing = { "p1" } };
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"), iconSaver: saver);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("https://psn/dragon.png", data.Achievements[0].UnlockedIconPath, "the icon download tries the address again");
            var snapshot = overlay.Links.Snapshots.Get(data.PlayniteGameId.Value, "PlayStation", "link-PlayStation");
            Assert.IsNull(snapshot.Rows.Single().Icon);
        }

        [TestMethod]
        public async Task MatchList_NeverStoresAnAddress()
        {
            var steamHunters = new FakeSource("SteamHunters", MetadataField.Categories)
            {
                Rows = { Row("s1", "Dragon Slayer", icon: "https://steam/dragon.png", category: "Main") }
            };
            var data = Game("Epic", Row("e1", "Dragon Slayer"));
            var overlay = Overlay(new[] { steamHunters }, Choices(categories: "SteamHunters"));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            var snapshot = overlay.Links.Snapshots.Get(data.PlayniteGameId.Value, "SteamHunters", "link-SteamHunters");
            Assert.AreEqual(1, snapshot.Rows.Count);
            Assert.IsNull(snapshot.Rows[0].Icon);
        }

        [TestMethod]
        public async Task SourceThatFailed_SavesNoIcons_AndKeepsItsMatchList()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            var saver = new FakeIconSaver();
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"), iconSaver: saver);
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            playstation.FailFetch = true;
            var next = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            next.Game = data.Game;
            next.PlayniteGameId = data.PlayniteGameId;
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(next.Game, next, CancellationToken.None);
            }

            Assert.AreEqual(1, saver.Saves.Count);
            Assert.AreEqual(0, saver.Deleted.Count);
            var snapshot = overlay.Links.Snapshots.Get(data.PlayniteGameId.Value, "PlayStation", "link-PlayStation");
            Assert.AreEqual(FakeIconSaver.PathOf("PlayStation", "p1"), snapshot.Rows.Single().Icon);
        }

        [TestMethod]
        public async Task SavedIcons_AreOverwritten_OnARelinkOrAnIconRefresh()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons)
            {
                Rows = { Row("p1", "Dragon Slayer", icon: "https://psn/dragon.png") }
            };
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
            var gameId = data.PlayniteGameId.Value;
            var saver = new FakeIconSaver();
            var overlay = Overlay(new[] { playstation }, Choices(icons: "PlayStation"), iconSaver: saver);

            async Task Refresh(bool forceIcons = false)
            {
                var next = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer", icon: "steam.png"));
                next.Game = data.Game;
                next.PlayniteGameId = gameId;
                using (var run = overlay.BeginRun())
                {
                    await run.ApplyAsync(next.Game, next, CancellationToken.None, forceIcons);
                }
            }

            await Refresh();
            await Refresh();
            overlay.Links.SetUser(gameId, "PlayStation", "other-game");
            await Refresh();
            await Refresh(forceIcons: true);

            CollectionAssert.AreEqual(
                new[] { ("PlayStation", false), ("PlayStation", false), ("PlayStation", true), ("PlayStation", true) },
                saver.Saves);
        }

        [TestMethod]
        public async Task CapstoneOnlySource_DeletesItsSavedIcons()
        {
            var playstation = new FakeSource("PlayStation", MetadataField.Icons | MetadataField.CapstoneArt);
            var platinum = Row("p0", "Master", icon: "https://psn/platinum.png");
            platinum.TrophyType = "platinum";
            playstation.Rows.Add(platinum);
            var data = Game("Steam", Row("ACH_DRAGON", "Dragon Slayer"));
            var saver = new FakeIconSaver();
            var overlay = Overlay(new[] { playstation }, Choices(capstoneArt: "PlayStation"), iconSaver: saver);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, saver.Saves.Count);
            CollectionAssert.AreEqual(new[] { "PlayStation" }, saver.Deleted);
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

        [TestMethod]
        public async Task ImplicitLink_JoinsTheGamesOwnRowsByApiName_AndStoresNoLink()
        {
            var hunters = new ImplicitSource("SteamHunters", MetadataField.Categories | MetadataField.Tags);
            hunters.Inner.Rows.Add(Row("ACH_A", "Different title", category: "Story", type: "DLC"));
            var data = Game("Steam", Row("ACH_A", "Own title", category: "Base set", type: "Missable"));
            data.AppId = 620;
            var overlay = Overlay(new IMetadataProvider[] { hunters }, Choices(categories: hunters.Key));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", data.Achievements[0].Category);
            Assert.AreEqual("Missable", data.Achievements[0].Tags, "tags were not chosen, so the platform's stay");
            CollectionAssert.AreEqual(new[] { "620" }, hunters.Inner.FetchedIds);
            Assert.IsFalse(overlay.Links.TryGet(data.PlayniteGameId.Value, hunters.Key, out _), "an implied link is never stored");
            Assert.AreEqual(0, hunters.Inner.ResolveCount);
        }

        [TestMethod]
        public async Task ImplicitLink_AUserLinkReplacesIt_AndABlankOneTurnsTheSourceOff()
        {
            var hunters = new ImplicitSource("SteamHunters", MetadataField.Categories);
            hunters.Inner.Rows.Add(Row("ACH_A", "One", category: "Story"));
            var data = Game("Steam", Row("ACH_A", "One"));
            data.AppId = 620;
            var overlay = Overlay(new IMetadataProvider[] { hunters }, Choices(categories: hunters.Key));
            overlay.Links.SetUser(data.PlayniteGameId.Value, hunters.Key, "999");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            CollectionAssert.AreEqual(new[] { "999" }, hunters.Inner.FetchedIds);

            overlay.Links.SetUser(data.PlayniteGameId.Value, hunters.Key, null);
            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(1, hunters.Inner.FetchCount, "a user's blank link fetches nothing");
        }

        [TestMethod]
        public async Task KeepStoredRun_AsksNoSource_AndKeepsTheStoredValues()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories | MetadataField.Tags);
            hunters.Rows.Add(Row("A", "One", category: "Fresh"));
            var data = Game("Epic", Row("e1", "One", category: "Own"));
            var stored = Game("Epic", Row("e1", "One", category: "Story", type: "Missable"));
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key, tags: hunters.Key), loadStored: _ => stored);
            overlay.Links.SetUser(data.PlayniteGameId.Value, hunters.Key, "link-SteamHunters");

            using (var run = overlay.BeginRun(keepStored: true))
            {
                Assert.IsTrue(run.KeepsStored);
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, hunters.FetchCount);
            Assert.AreEqual(0, hunters.ResolveCount);
            Assert.AreEqual("Story", data.Achievements[0].Category);
            Assert.AreEqual("Missable", data.Achievements[0].Tags);
        }

        [TestMethod]
        public async Task KeepStoredGame_AsksNoSource_InAnOrdinaryRun()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories);
            hunters.Rows.Add(Row("A", "One", category: "Fresh"));
            var data = Game("Epic", Row("e1", "One", category: "Own"));
            var stored = Game("Epic", Row("e1", "One", category: "Story"));
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Key), loadStored: _ => stored);
            overlay.Links.SetUser(data.PlayniteGameId.Value, hunters.Key, "link-SteamHunters");

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None, keepStored: true);
            }

            Assert.AreEqual(0, hunters.FetchCount);
            Assert.AreEqual("Story", data.Achievements[0].Category);
        }

        [TestMethod]
        public async Task Tags_FromASource_KeepThePlatformsUnobtainable()
        {
            var truegaming = new FakeSource("TrueGaming", MetadataField.Tags);
            truegaming.Rows.Add(Row("A", "One", type: "Missable"));
            var archived = Row("s1", "One", type: "Unobtainable");
            var data = Game("Steam", archived);

            using (var run = Overlay(new[] { truegaming }, Choices(tags: truegaming.Key)).BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            CollectionAssert.AreEquivalent(
                new[] { "Missable", "Unobtainable" },
                PlayniteAchievements.Services.Achievements.AchievementTagHelper.Parse(archived.Tags));
        }

        /// <summary>A source that links a Steam game by its own appId, joining its rows by ApiName.</summary>
        private sealed class ImplicitSource : IMetadataProvider, IMetadataImplicitLink
        {
            public ImplicitSource(string key, MetadataField fields)
            {
                Inner = new FakeSource(key, fields);
            }

            public FakeSource Inner { get; }

            public string Key => Inner.Key;

            public string DisplayNameKey => Inner.DisplayNameKey;

            public MetadataField SupportedFields => Inner.SupportedFields;

            public bool IsNativeFor(string servicingProviderKey) => false;

            public Task<bool> BeginRunAsync(CancellationToken ct) => Inner.BeginRunAsync(ct);

            public void EndRun() => Inner.EndRun();

            public Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct) =>
                Inner.ResolveLinkAsync(request, ct);

            public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
            {
                var set = await Inner.FetchAsync(request, linkId, ct);
                set.SharesApiNames = true;
                set.KeepsNoMatchList = true;
                return set;
            }

            public string ImplicitLinkId(MetadataLinkRequest request) =>
                string.Equals(request?.ServicingProviderKey, "Steam", StringComparison.OrdinalIgnoreCase) && request.Data?.AppId > 0
                    ? request.Data.AppId.ToString()
                    : null;

            public string ImplicitLinkId(Guid gameId) => null;
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

            public MetadataField SupportedFields { get; }

            public bool IdsMayBeShared { get; set; }

            public bool FoundByName { get; set; }

            /// <summary>The fetch throws, as a source does when its site cannot be reached.</summary>
            public bool FailFetch { get; set; }

            /// <summary>Fields the fetched set reports it could not read.</summary>
            public MetadataField Unavailable { get; set; }

            /// <summary>The fuller link the fetched set reports.</summary>
            public string ExtendedId { get; set; }

            /// <summary>Whether the fetched rows' text is in the plugin's language.</summary>
            public bool TextInPluginLanguage { get; set; } = true;

            /// <summary>The fields the last fetch was asked for.</summary>
            public MetadataField LastWanted { get; private set; }

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
                if (UnreadableLookup)
                {
                    return Task.FromResult(MetadataSourceLink.Unavailable());
                }

                if (ResolveWhen != null && !ResolveWhen(request))
                {
                    return Task.FromResult<MetadataSourceLink>(null);
                }

                return Task.FromResult(new MetadataSourceLink(ResolvedId ?? "link-" + Key, ResolvedLabel, FoundByName) { Matched = ResolvedMatched });
            }

            /// <summary>The link the lookup finds; "link-" and the key when unset.</summary>
            public string ResolvedId { get; set; }

            /// <summary>The match count the lookup reports.</summary>
            public int? ResolvedMatched { get; set; }

            /// <summary>The lookup hits pages it cannot read, as behind a challenge page.</summary>
            public bool UnreadableLookup { get; set; }

            /// <summary>When set, the source finds the game only for a request this accepts.</summary>
            public Func<MetadataLinkRequest, bool> ResolveWhen { get; set; }

            /// <summary>The names the source reports finding the game under.</summary>
            public string ResolvedLabel { get; set; }

            public Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
            {
                FetchCount++;
                LastWanted = request.WantedFields;
                if (FailFetch)
                {
                    throw new InvalidOperationException("unreachable");
                }

                FetchedIds.Add(linkId);
                var rows = RowsByLink.TryGetValue(linkId, out var listed) ? listed : Rows;
                var set = new AchievementSourceSet(Key, linkId, rows)
                {
                    ApiNamesMayBeShared = IdsMayBeShared,
                    UnavailableFields = Unavailable,
                    ExtendedLinkId = ExtendedId,
                    TextInPluginLanguage = TextInPluginLanguage,
                    GameArtUrl = GameArtByLink.TryGetValue(linkId, out var gameArt) ? gameArt : null,
                    EnglishRows = EnglishRows == null
                        ? null
                        : new Func<CancellationToken, Task<IList<AchievementDetail>>>(token =>
                        {
                            EnglishRowsLoads++;
                            return Task.FromResult<IList<AchievementDetail>>(EnglishRows);
                        })
                };
                foreach (var art in CategoryArt)
                {
                    set.CategoryArtUrls[art.Key] = art.Value;
                }

                return Task.FromResult(set);
            }

            public Dictionary<string, string> CategoryArt { get; } = new Dictionary<string, string>();

            /// <summary>The game image a list id's page gives.</summary>
            public Dictionary<string, string> GameArtByLink { get; } = new Dictionary<string, string>();

            /// <summary>Rows for one list id; any other id gets <see cref="Rows"/>.</summary>
            public Dictionary<string, List<AchievementDetail>> RowsByLink { get; } = new Dictionary<string, List<AchievementDetail>>();

            /// <summary>The ids each fetch was asked for, in order.</summary>
            public List<string> FetchedIds { get; } = new List<string>();

            /// <summary>The rows in English, by the same ApiNames; null when the rows already are.</summary>
            public List<AchievementDetail> EnglishRows { get; set; }

            public int EnglishRowsLoads { get; private set; }
        }

        /// <summary>A servicing provider's English titles for every game, counting each load.</summary>
        private sealed class FakeEnglishTitles
        {
            public List<AchievementDetail> Rows { get; } = new List<AchievementDetail>();

            public int Loads { get; private set; }

            public Task<IList<AchievementDetail>> FetchAsync(Game game, GameAchievementData data, CancellationToken ct)
            {
                Loads++;
                return Task.FromResult<IList<AchievementDetail>>(Rows);
            }
        }

        private static PersistedSettings German(PersistedSettings settings)
        {
            settings.GlobalLanguage = "german";
            return settings;
        }

        [TestMethod]
        public async Task EnglishTitles_PairWhatEveryOtherWayLeft()
        {
            // A German game against an English-only site: one title is the same in both, the rest
            // pair only through the game's English titles.
            var site = new FakeSource("TrueGaming", MetadataField.Categories);
            site.Rows.Add(Row("t1", "Dragon Slayer", category: "Story"));
            site.Rows.Add(Row("t2", "Collector", category: "Collectibles"));
            site.Rows.Add(Row("t3", "Excalibur", category: "Weapons"));
            var dragon = Row("g1", "Drachentöter");
            var coins = Row("g2", "Sammler");
            var sword = Row("g3", "Excalibur");
            var data = Game("GOG", dragon, coins, sword);
            var english = new FakeEnglishTitles();
            english.Rows.Add(Row("g1", "Dragon Slayer"));
            english.Rows.Add(Row("g2", "Collector"));
            english.Rows.Add(Row("g3", "Excalibur"));
            var overlay = Overlay(new[] { site }, German(Choices(categories: site.Key)), englishTitles: english.FetchAsync);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", dragon.Category);
            Assert.AreEqual("Collectibles", coins.Category);
            Assert.AreEqual("Weapons", sword.Category);
            Assert.AreEqual("Drachentöter", dragon.DisplayName, "the English titles are never shown");
            Assert.AreEqual(3, overlay.Links.GetCoverage(data.PlayniteGameId.Value, site.Key).Matched);
            Assert.AreEqual(1, english.Loads);
        }

        [TestMethod]
        public async Task EnglishTitles_AreNotLoaded_WhenTheTitlesAlreadyPair()
        {
            var site = new FakeSource("TrueGaming", MetadataField.Categories) { EnglishRows = new List<AchievementDetail>() };
            site.Rows.Add(Row("t1", "Excalibur", category: "Weapons"));
            var data = Game("GOG", Row("g1", "Excalibur"));
            var english = new FakeEnglishTitles();
            var overlay = Overlay(new[] { site }, German(Choices(categories: site.Key)), englishTitles: english.FetchAsync);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, english.Loads);
            Assert.AreEqual(0, site.EnglishRowsLoads);
        }

        [TestMethod]
        public async Task EnglishTitles_AreNotLoaded_WhenTheLanguageIsEnglish()
        {
            var site = new FakeSource("TrueGaming", MetadataField.Categories);
            site.Rows.Add(Row("t1", "Dragon Slayer", category: "Story"));
            var dragon = Row("g1", "Dragon Killer");
            var data = Game("GOG", dragon);
            var english = new FakeEnglishTitles();
            english.Rows.Add(Row("g1", "Dragon Slayer"));
            var overlay = Overlay(new[] { site }, Choices(categories: site.Key), englishTitles: english.FetchAsync);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, english.Loads);
            Assert.IsNull(dragon.Category);
        }

        [TestMethod]
        public async Task EnglishTitles_AreNotLoaded_WhenNoSourceRowIsLeft()
        {
            // Every row the site has is paired; the game's extra achievement has nothing to pair with.
            var site = new FakeSource("TrueGaming", MetadataField.Categories);
            site.Rows.Add(Row("t1", "Excalibur", category: "Weapons"));
            var data = Game("GOG", Row("g1", "Excalibur"), Row("g2", "Nur auf GOG"));
            var english = new FakeEnglishTitles();
            var overlay = Overlay(new[] { site }, German(Choices(categories: site.Key)), englishTitles: english.FetchAsync);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual(0, english.Loads);
        }

        [TestMethod]
        public async Task EnglishTitles_LeaveOutWhatTheUserKeptOut()
        {
            var site = new FakeSource("TrueGaming", MetadataField.Categories);
            site.Rows.Add(Row("t1", "Dragon Slayer", category: "Story"));
            var dragon = Row("g1", "Drachentöter");
            var data = Game("GOG", dragon);
            var english = new FakeEnglishTitles();
            english.Rows.Add(Row("g1", "Dragon Slayer"));
            var overlay = Overlay(new[] { site }, German(Choices(categories: site.Key)), englishTitles: english.FetchAsync);
            overlay.Links.SetUser(data.PlayniteGameId.Value, site.Key, "link-TrueGaming");
            overlay.Links.SetPairing(data.PlayniteGameId.Value, site.Key, "g1", string.Empty);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.IsNull(dragon.Category);
        }

        [TestMethod]
        public async Task EnglishRows_OfASourceInAnotherLanguage_PairWithTheGamesEnglish()
        {
            // The site translates differently from the game; both read alike in English.
            var site = new FakeSource("PlayStation", MetadataField.Categories);
            site.Rows.Add(Row("p1", "Drachenjäger", category: "Story"));
            site.EnglishRows = new List<AchievementDetail> { Row("p1", "Dragon Slayer") };
            var dragon = Row("g1", "Drachentöter");
            var data = Game("GOG", dragon);
            var english = new FakeEnglishTitles();
            english.Rows.Add(Row("g1", "Dragon Slayer"));
            var overlay = Overlay(new[] { site }, German(Choices(categories: site.Key)), englishTitles: english.FetchAsync);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", dragon.Category);
            Assert.AreEqual(1, site.EnglishRowsLoads);
        }

        [TestMethod]
        public async Task NameFoundLink_IsKept_WhenItsAchievementsPairInEnglish()
        {
            var site = new FakeSource("TrueGaming", MetadataField.Categories) { FoundByName = true };
            site.Rows.Add(Row("t1", "Dragon Slayer", category: "Story"));
            site.Rows.Add(Row("t2", "Collector", category: "Collectibles"));
            var data = Game("GOG", Row("g1", "Drachentöter"), Row("g2", "Sammler"));
            var english = new FakeEnglishTitles();
            english.Rows.Add(Row("g1", "Dragon Slayer"));
            english.Rows.Add(Row("g2", "Collector"));
            var overlay = Overlay(new[] { site }, German(Choices(categories: site.Key)), englishTitles: english.FetchAsync);

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.IsTrue(overlay.Links.TryGet(data.PlayniteGameId.Value, site.Key, out var link));
            Assert.IsFalse(link.IsNoMatch);
            Assert.AreEqual("Story", data.Achievements[0].Category);
            Assert.AreEqual(1, english.Loads);
        }

        [TestMethod]
        public async Task NoMatchLink_IsLookedForAgain_WhenTheLanguageChanges_AndAFoundLinkIsKept()
        {
            var settings = Choices(categories: "TrueGaming", icons: "PlayStation");
            var missing = new FakeSource("TrueGaming", MetadataField.Categories)
            {
                ResolveWhen = request => false
            };
            var found = new FakeSource("PlayStation", MetadataField.Icons);
            found.Rows.Add(Row("p1", "One", icon: "https://psn/one.png"));
            var game = new Game("Test game") { Id = Guid.NewGuid() };
            var overlay = Overlay(new IMetadataProvider[] { missing, found }, settings);

            async Task RefreshAsync()
            {
                var data = new GameAchievementData
                {
                    ProviderKey = "GOG",
                    PlayniteGameId = game.Id,
                    Game = game,
                    Achievements = new List<AchievementDetail> { Row("g1", "One") }
                };
                using (var run = overlay.BeginRun())
                {
                    await run.ApplyAsync(game, data, CancellationToken.None);
                }
            }

            await RefreshAsync();
            await RefreshAsync();
            Assert.AreEqual(1, missing.ResolveCount);
            Assert.AreEqual(1, found.ResolveCount);

            settings.GlobalLanguage = "german";
            await RefreshAsync();
            Assert.AreEqual(2, missing.ResolveCount, "a no-match is looked for again in the new language");
            Assert.AreEqual(1, found.ResolveCount, "a found link is kept");

            await RefreshAsync();
            Assert.AreEqual(2, missing.ResolveCount, "and only once");
        }
    }
}
