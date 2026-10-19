using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
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
        public async Task Categories_MergeKeepsProviderNamedCategory_AndAddsTypes()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { Mode = MetadataCategoryMode.Merge };
            hunters.Rows.Add(Row("A", "One", category: "Story", type: "Missable"));
            hunters.Rows.Add(Row("B", "Two", category: "DLC Pack", type: "DLC"));
            var named = Row("e1", "One", category: "Chapter 1");
            var plain = Row("e2", "Two");
            var data = Game("Epic", named, plain);
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Mode));

            using (var run = overlay.BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Chapter 1", named.Category);
            StringAssert.Contains(named.CategoryType, "Missable");
            Assert.AreEqual("DLC Pack", plain.Category);
            StringAssert.Contains(plain.CategoryType, "DLC");
        }

        [TestMethod]
        public async Task Categories_OverrideReplacesProviderCategory()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { Mode = MetadataCategoryMode.Override };
            hunters.Rows.Add(Row("A", "One", category: "Story"));
            var named = Row("e1", "One", category: "Chapter 1", type: "Missable");
            var data = Game("Epic", named);

            using (var run = Overlay(new[] { hunters }, Choices(categories: hunters.Mode)).BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", named.Category);
            Assert.IsFalse((named.CategoryType ?? string.Empty).Contains("Missable"));
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
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { Mode = MetadataCategoryMode.Override, IdsMayBeShared = true };
            hunters.Rows.Add(Row("ACH_DRAGON", "Dragon Slayer", category: "Story"));
            hunters.Rows.Add(Row("ACH_COINS", "Collector", category: "Collectibles"));
            hunters.Rows.Add(Row("ACH_EXTRA", "Extra", category: "DLC"));
            var dragon = Row("ACH_DRAGON", "Drachentöter");
            var coins = Row("ACH_COINS", "Sammler");
            var unknown = Row("GOG_ONLY", "Nur auf GOG");
            var data = Game("GOG", dragon, coins, unknown);
            var overlay = Overlay(new[] { hunters }, Choices(categories: hunters.Mode));

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
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories) { Mode = MetadataCategoryMode.Override, IdsMayBeShared = true };
            hunters.Rows.Add(Row("ACH_ONE", "One", category: "Story"));
            hunters.Rows.Add(Row("ACH_TWO", "Two", category: "Side"));
            // One of three distinctive IDs coincides (33%), under the threshold; "ACH_TWO" names a
            // different achievement here, so an ID join would mislabel it.
            var one = Row("e_one", "One");
            var two = Row("ACH_TWO", "Something else");
            var three = Row("e_three", "Two");
            var data = Game("Epic", one, two, three);

            using (var run = Overlay(new[] { hunters }, Choices(categories: hunters.Mode)).BeginRun())
            {
                await run.ApplyAsync(data.Game, data, CancellationToken.None);
            }

            Assert.AreEqual("Story", one.Category);
            Assert.IsNull(two.Category);
            Assert.AreEqual("Side", three.Category);
        }

        [TestMethod]
        public async Task PlatformChoice_OverridesTheDefaults()
        {
            var hunters = new FakeSource("SteamHunters", MetadataField.Categories);
            hunters.Rows.Add(Row("A", "One", category: "Story"));
            var settings = new PersistedSettings();
            settings.MetadataPlatformChoices = new Dictionary<string, MetadataChoices>
            {
                ["Xbox"] = new MetadataChoices { Categories = "SteamHunters", CategoryMode = MetadataCategoryMode.Override }
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
            MetadataCategoryMode? categories = null,
            string capstoneArt = null)
        {
            var settings = new PersistedSettings();
            settings.MetadataDefaults = new MetadataChoices
            {
                Icons = icons ?? string.Empty,
                Categories = categories.HasValue ? "SteamHunters" : string.Empty,
                CategoryMode = categories ?? MetadataCategoryMode.Merge,
                Rarity = string.Empty,
                CapstoneArt = capstoneArt ?? string.Empty
            };
            return settings;
        }

        private MetadataOverlay Overlay(IReadOnlyList<IMetadataProvider> providers, PersistedSettings settings)
        {
            return new MetadataOverlay(() => providers, () => settings, new MetadataLinkStore(_directory, null), null);
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
                CategoryType = type
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

            public MetadataField SupportedFields { get; }

            /// <summary>The category mode the test's settings choose for this source.</summary>
            public MetadataCategoryMode Mode { get; set; }

            public bool IdsMayBeShared { get; set; }

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
                return Task.FromResult(new MetadataSourceLink("link-" + Key));
            }

            public Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
            {
                FetchCount++;
                return Task.FromResult(new AchievementSourceSet(Key, linkId, Rows) { ApiNamesMayBeShared = IdsMayBeShared });
            }
        }
    }
}
