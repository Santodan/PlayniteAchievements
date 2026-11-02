using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Providers.Metadata.Exophase;
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
    public class MetadataConsoleVersionTests
    {
        private string _directory;

        [TestInitialize]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "pa-console-version-" + Guid.NewGuid().ToString("N"));
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
        public void Newest_SearchesAndBuildsNewestFirst()
        {
            CollectionAssert.AreEqual(
                new[] { "ps5", "ps4", "ps3", "psvita" },
                ExophaseMetadataBranch.PlayStation.SearchPlatforms(MetadataConsoleVersion.Newest).ToList());
            CollectionAssert.AreEqual(
                new[] { "-psn", "-ps4", "-ps3", "-psvita" },
                ExophaseMetadataBranch.PlayStation.PageSuffixes(MetadataConsoleVersion.Newest).ToList());
            CollectionAssert.AreEqual(
                new[] { "xbox", "xbox-one", "xbox-360" },
                ExophaseMetadataBranch.Xbox.SearchPlatforms(MetadataConsoleVersion.Newest).ToList());
            CollectionAssert.AreEqual(
                new[] { "-xbox", "-xbox-one", "-xbox-360" },
                ExophaseMetadataBranch.Xbox.PageSuffixes(MetadataConsoleVersion.Newest).ToList());
        }

        [TestMethod]
        public void Oldest_SearchesAndBuildsOldestFirst_WithVitaLast()
        {
            CollectionAssert.AreEqual(
                new[] { "ps3", "ps4", "ps5", "psvita" },
                ExophaseMetadataBranch.PlayStation.SearchPlatforms(MetadataConsoleVersion.Oldest).ToList());
            CollectionAssert.AreEqual(
                new[] { "-ps3", "-ps4", "-psn", "-psvita" },
                ExophaseMetadataBranch.PlayStation.PageSuffixes(MetadataConsoleVersion.Oldest).ToList());
            CollectionAssert.AreEqual(
                new[] { "xbox-360", "xbox-one", "xbox" },
                ExophaseMetadataBranch.Xbox.SearchPlatforms(MetadataConsoleVersion.Oldest).ToList());
            CollectionAssert.AreEqual(
                new[] { "-xbox-360", "-xbox-one", "-xbox" },
                ExophaseMetadataBranch.Xbox.PageSuffixes(MetadataConsoleVersion.Oldest).ToList());
        }

        [TestMethod]
        public void OwnPlatformBranch_HasNoConsoles()
        {
            Assert.IsFalse(ExophaseMetadataBranch.Own.HasConsoles);
            Assert.AreEqual(0, ExophaseMetadataBranch.Own.SearchPlatforms(MetadataConsoleVersion.Oldest).Count);
            Assert.AreEqual(0, ExophaseMetadataBranch.Own.PreferredVersions("game-ps4", Related(("PS3", "game-ps3")), MetadataConsoleVersion.Oldest).Count);
        }

        [TestMethod]
        public void PreferredVersions_Newest_MovesAPs4MatchToPs5()
        {
            var related = Related(("PS5", "game-psn"), ("PS3", "game-ps3"));

            var preferred = ExophaseMetadataBranch.PlayStation.PreferredVersions("game-ps4", related, MetadataConsoleVersion.Newest);

            CollectionAssert.AreEqual(new[] { "game-psn" }, preferred.Select(page => page.Slug).ToList());
        }

        [TestMethod]
        public void PreferredVersions_Oldest_MovesAPs4MatchToPs3()
        {
            var related = Related(("PS5", "game-psn"), ("PS3", "game-ps3"));

            var preferred = ExophaseMetadataBranch.PlayStation.PreferredVersions("game-ps4", related, MetadataConsoleVersion.Oldest);

            CollectionAssert.AreEqual(new[] { "game-ps3" }, preferred.Select(page => page.Slug).ToList());
        }

        [TestMethod]
        public void PreferredVersions_SharedEnding_ReadsTheConsoleFromWhatIsNotLinked()
        {
            // "-psn" is PS4 or PS5; with PS4 linked, the match is the PS5 page.
            var related = Related(("PS4", "game-ps4"), ("PS3", "game-ps3"));

            Assert.AreEqual(0, ExophaseMetadataBranch.PlayStation.PreferredVersions("game-psn", related, MetadataConsoleVersion.Newest).Count);
            CollectionAssert.AreEqual(
                new[] { "game-ps3", "game-ps4" },
                ExophaseMetadataBranch.PlayStation.PreferredVersions("game-psn", related, MetadataConsoleVersion.Oldest).Select(page => page.Slug).ToList());
        }

        [TestMethod]
        public void PreferredVersions_Oldest_MovesAnXboxSeriesMatchTo360()
        {
            var related = Related(("Xbox 360", "game-xbox-360"), ("Xbox One", "game-xbox-one"));

            var preferred = ExophaseMetadataBranch.Xbox.PreferredVersions("game-xbox", related, MetadataConsoleVersion.Oldest);

            Assert.AreEqual("game-xbox-360", preferred.First().Slug);
        }

        [TestMethod]
        public async Task Resolver_DefaultPreference_KeepsTheLinkWithoutLookingAgain()
        {
            var (resolver, _, game) = Setup();
            var source = new PreferenceSource { Next = "game-psn" };
            await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);

            source.Next = "game-ps3";
            var (id, _, _) = await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);

            Assert.AreEqual("game-psn", id);
            Assert.AreEqual(1, source.ResolveCount);
        }

        [TestMethod]
        public async Task Resolver_PreferenceChange_TakesThePreferredPageOnATie()
        {
            var (resolver, state, game) = Setup();
            var source = new PreferenceSource { Next = "game-psn" };
            await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);
            state.RecordApplied(game.Id, null, null, Coverage(source.Key, 40));

            source.LookupPreference = "consoles:oldest";
            source.Next = "game-ps3";
            source.NextMatched = 40;
            var (id, _, _) = await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);

            Assert.AreEqual("game-ps3", id);
            Assert.AreEqual(2, source.ResolveCount);

            // Back to the default looks again, and the newest page wins its tie the same way.
            source.LookupPreference = null;
            source.Next = "game-psn";
            (id, _, _) = await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);

            Assert.AreEqual("game-psn", id);
            Assert.AreEqual(3, source.ResolveCount);
        }

        [TestMethod]
        public async Task Resolver_SearchChange_StillNeedsMoreMatchesToRelink()
        {
            var (resolver, state, game) = Setup();
            var source = new PreferenceSource { Next = "game-psn" };
            await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);
            state.RecordApplied(game.Id, null, null, Coverage(source.Key, 40));

            game.Name = "Renamed game";
            source.Next = "game-ps3";
            source.NextMatched = 40;
            var (id, _, _) = await resolver.ResolveAsync(source, game.Id, Request(game), CancellationToken.None);

            Assert.AreEqual("game-psn", id);
            Assert.AreEqual(2, source.ResolveCount);
        }

        private (MetadataLinkResolver Resolver, MetadataStateStore State, Game Game) Setup()
        {
            var database = new FakeMetadataStateDatabase();
            var state = MetadataTestStores.State(_directory, database);
            var links = new MetadataLinkStore(new GameCustomDataStore(_directory), state, null);
            return (new MetadataLinkResolver(links, null), state, new Game("Test game") { Id = Guid.NewGuid() });
        }

        private static MetadataLinkRequest Request(Game game) => new MetadataLinkRequest(game, null);

        private static IReadOnlyDictionary<string, MetadataCoverage> Coverage(string key, int matched) =>
            new Dictionary<string, MetadataCoverage>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = new MetadataCoverage { Matched = matched, Total = matched }
            };

        private static IReadOnlyList<ExophaseRelatedPage> Related(params (string Platform, string Slug)[] pages) =>
            pages.Select(page => new ExophaseRelatedPage(page.Platform, page.Slug)).ToList();

        private sealed class PreferenceSource : IMetadataProvider, IMetadataLookupPreference
        {
            public string Key => "PlayStation";

            public string DisplayNameKey => Key;

            public MetadataField SupportedFields => MetadataField.Icons;

            public string LookupPreference { get; set; }

            /// <summary>The link the next lookup finds.</summary>
            public string Next { get; set; }

            /// <summary>How many achievements the next lookup's pages match; null when it does not judge them.</summary>
            public int? NextMatched { get; set; }

            public int ResolveCount { get; private set; }

            public bool IsNativeFor(string servicingProviderKey) => false;

            public Task<bool> BeginRunAsync(CancellationToken ct) => Task.FromResult(true);

            public void EndRun()
            {
            }

            public Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
            {
                ResolveCount++;
                return Task.FromResult(new MetadataSourceLink(Next) { Matched = NextMatched });
            }

            public Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct) =>
                Task.FromResult<AchievementSourceSet>(null);
        }
    }
}
