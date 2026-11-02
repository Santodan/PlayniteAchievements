using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>Links that name several lists: parsing, display, fetching and matching list by list.</summary>
    [TestClass]
    [TestCategory("Services")]
    public class MetadataLinkListTests
    {
        private static AchievementDetail Row(string apiName, string title, string category = null) =>
            new AchievementDetail { ApiName = apiName, DisplayName = title, Category = category };

        [TestMethod]
        public void Parse_TrimsDropsBlanksAndRepeats_KeepingOrder()
        {
            CollectionAssert.AreEqual(new[] { "a", "B" }, MetadataLinkIds.Parse(" a, ,B ,a,b ").ToList());
            Assert.AreEqual(0, MetadataLinkIds.Parse(null).Count);
            Assert.AreEqual(0, MetadataLinkIds.Parse(" , ").Count);
        }

        [TestMethod]
        public void Join_IsCommaAndSpace_WithoutBlanksOrRepeats()
        {
            Assert.AreEqual("a, b", MetadataLinkIds.Join(new[] { "a", " b ", null, "A" }));
            Assert.IsNull(MetadataLinkIds.Join(new string[0]));
            Assert.IsNull(MetadataLinkIds.Join(null));
        }

        [DataTestMethod]
        [DataRow("b, a, c", "a,b", true)]
        [DataRow("a,b", "b,a", true)]
        [DataRow("a", "a,b", false)]
        [DataRow("a", "", false)]
        [DataRow("A", "a", true)]
        public void ContainsAll_IgnoresOrderAndCase(string now, string earlier, bool expected)
        {
            Assert.AreEqual(expected, MetadataLinkIds.ContainsAll(now, earlier));
        }

        [TestMethod]
        public void ShownLink_ShowsEachListAsItsSourceDoes()
        {
            var details = new FakeDetails();

            Assert.AreEqual("game-1, game-2", MetadataLinkIds.ShownLink(details, "id:game-1, id:game-2"));
            Assert.AreEqual("game-1", MetadataLinkIds.ShownLink(details, "id:game-1"));
            Assert.IsNull(MetadataLinkIds.ShownLink(null, "a"));
        }

        [TestMethod]
        public void ShownLink_CollapsesListsTheSourceShowsAlike()
        {
            // A link stored before links were one name per game ("ta:Hades, tt:Hades-PS5").
            Assert.AreEqual("game", MetadataLinkIds.ShownLink(new FakeDetails(), "id:game, game"));
        }

        [TestMethod]
        public void ShownLink_OfARepeatedList_IsThatList()
        {
            Assert.AreEqual("game", MetadataLinkIds.ShownLink(new FakeDetails(), "id:game, id:game"));
        }

        [TestMethod]
        public void DescribeLink_JoinsEachListsLines_SkippingListsWithNone()
        {
            var details = new FakeDetails { Described = { ["a"] = "A page", ["c"] = "C page" } };

            Assert.AreEqual("A page" + Environment.NewLine + "C page", MetadataLinkIds.DescribeLink(details, Guid.Empty, "a, b, c"));
            Assert.IsNull(MetadataLinkIds.DescribeLink(details, Guid.Empty, "b, d"));
        }

        [TestMethod]
        public void PageLinks_ListEachListsPagesInOrder()
        {
            var pages = new FakePages();

            var links = MetadataLinkIds.PageLinks(pages, Guid.Empty, "a, b", "Steam");

            CollectionAssert.AreEqual(new[] { "a", "b" }, links.Select(link => link.Label).ToList());
            Assert.AreEqual(0, MetadataLinkIds.PageLinks(null, Guid.Empty, "a", "Steam").Count);
        }

        [TestMethod]
        public async Task Fetch_OneList_PassesTheLinkAsStored()
        {
            var source = new FakeSource { Rows = { ["a"] = new List<AchievementDetail> { Row("1", "One") } } };

            var set = await MetadataListFetch.FetchAsync(source, Request(), "a", null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "a" }, source.Fetched);
            Assert.AreEqual(0, set.Lists.Count);
        }

        [TestMethod]
        public async Task Fetch_OneListWithAStrayComma_PassesTheList()
        {
            var source = new FakeSource { Rows = { ["a"] = new List<AchievementDetail> { Row("1", "One") } } };

            await MetadataListFetch.FetchAsync(source, Request(), "a,", null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "a" }, source.Fetched);
        }

        [TestMethod]
        public async Task Fetch_SeveralLists_JoinsTheirRowsInOrder_KeepingEachList()
        {
            var source = new FakeSource
            {
                Rows =
                {
                    ["a"] = new List<AchievementDetail> { Row("1", "One") },
                    ["b"] = new List<AchievementDetail> { Row("2", "Two"), Row("3", "Three") }
                },
                Titles = { ["b"] = "Game B" }
            };

            var set = await MetadataListFetch.FetchAsync(source, Request(), "a, b", null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, set.Achievements.Select(row => row.ApiName).ToList());
            CollectionAssert.AreEqual(new[] { "a", "Game B" }, set.Lists.Select(list => list.Title).ToList());
            Assert.AreEqual(2, set.Lists[1].Rows.Count);
            Assert.AreEqual("a, b", set.LinkId);
            Assert.IsNull(set.ExtendedLinkId);
        }

        [TestMethod]
        public async Task Fetch_AListThatGivesNothing_FailsTheWholeLink()
        {
            var source = new FakeSource { Rows = { ["a"] = new List<AchievementDetail> { Row("1", "One") } } };

            Assert.IsNull(await MetadataListFetch.FetchAsync(source, Request(), "a, missing", null, CancellationToken.None));
        }

        [TestMethod]
        public void NameBaseCategories_FilesEachListsBaseRowsUnderItsGame_KeepingDlcAndOwnNames()
        {
            AchievementDetail Kind(AchievementDetail row, string kind)
            {
                row.CategoryKind = kind;
                return row;
            }

            var first = Set("me1", Kind(Row("1", "Saren", "Mass Effect Legendary Edition"), "Base"), Kind(Row("2", "Pinnacle", "Pinnacle Station"), "DLC"));
            var second = Set("me2", Kind(Row("3", "Suicide Mission", "Mass Effect Legendary Edition"), "Base"), Kind(Row("4", "Grunt", "Mass Effect 2 (SteamHunters)"), "Base"));

            MetadataListFetch.NameBaseCategories(
                new[] { ("me1", first), ("me2", second) },
                MetadataFoundNames.Parse("Mass Effect\tMass Effect\nMass Effect 2"),
                "Mass Effect Legendary Edition",
                "Mass Effect Legendary Edition");

            Assert.AreEqual("Mass Effect", first.Achievements[0].Category);
            Assert.AreEqual("Pinnacle Station", first.Achievements[1].Category);
            Assert.AreEqual("Mass Effect 2", second.Achievements[0].Category);
            Assert.AreEqual("Mass Effect 2 (SteamHunters)", second.Achievements[1].Category);
        }

        [TestMethod]
        public void NameBaseCategories_LeavesOneListAndLabelsOfAnotherCount()
        {
            var only = Set("a", Row("1", "One", "Game"));
            only.Achievements[0].CategoryKind = "Base";
            MetadataListFetch.NameBaseCategories(new[] { ("a", only) }, MetadataFoundNames.Parse("A"), "Game", "Game");
            Assert.AreEqual("Game", only.Achievements[0].Category);

            var a = Set("a", Row("1", "One", "Game"));
            var b = Set("b", Row("2", "Two", "Game"));
            a.Achievements[0].CategoryKind = "Base";
            b.Achievements[0].CategoryKind = "Base";
            b.Title = "Game B";
            MetadataListFetch.NameBaseCategories(new[] { ("a", a), ("b", b) }, MetadataFoundNames.Parse("A"), "Game", "Game");
            Assert.AreEqual("Game", a.Achievements[0].Category);
            Assert.AreEqual("Game B", b.Achievements[0].Category);
        }

        [TestMethod]
        public void Combine_ExtendsTheLink_WhenAListIsStoredUnderAFullerId()
        {
            var fetched = new List<(string, AchievementSourceSet)>
            {
                ("a", Set("a", Row("1", "One"))),
                ("b-full", Set("b", Row("2", "Two")))
            };

            var set = MetadataListFetch.Combine("Source", "a, b", new[] { "a", "b" }, fetched);

            Assert.AreEqual("a, b-full", set.ExtendedLinkId);
        }

        [TestMethod]
        public void Combine_MergesFlagsAndArt_FirstListWinningArt()
        {
            var first = Set("a", Row("1", "One"));
            first.SharesApiNames = true;
            first.UnavailableFields = MetadataField.Tags;
            first.CategoryArtUrls["DLC"] = "first.png";
            var second = Set("b", Row("2", "Two"));
            second.ApiNamesMayBeShared = true;
            second.UnavailableFields = MetadataField.Points;
            second.CategoryArtUrls["DLC"] = "second.png";
            second.CategoryArtUrls["Other"] = "other.png";

            var set = MetadataListFetch.Combine("Source", "a, b", new[] { "a", "b" }, new[] { ("a", first), ("b", second) });

            Assert.IsFalse(set.SharesApiNames);
            Assert.IsTrue(set.ApiNamesMayBeShared);
            Assert.AreEqual(MetadataField.Tags | MetadataField.Points, set.UnavailableFields);
            Assert.AreEqual("first.png", set.CategoryArtUrls["DLC"]);
            Assert.AreEqual("other.png", set.CategoryArtUrls["Other"]);
        }

        [TestMethod]
        public void MatchByList_LeavesUnmatchedRowsToEveryList()
        {
            // "Bonus" is in the category matched to the first list but listed only on the second.
            var targets = new List<AchievementDetail>
            {
                Row("t1", "Saren", "Game 1"),
                Row("t2", "Spectre", "Game 1"),
                Row("t3", "Bonus", "Game 1")
            };
            var first = new List<AchievementDetail> { Row("s1", "Saren"), Row("s2", "Spectre") };
            var second = new List<AchievementDetail> { Row("s3", "Bonus") };
            var lists = new[] { new MetadataSourceList("a", "A", first), new MetadataSourceList("b", "B", second) };

            var matches = MetadataOverlayRun.MatchByList(targets, first.Concat(second).ToList(), lists, matchApiNames: false);

            Assert.AreEqual(3, matches.Count);
        }

        [TestMethod]
        public void MatchByList_WithOneList_IsPlainMatching()
        {
            var targets = new List<AchievementDetail> { Row("t1", "Saren", "Game 1") };
            var sources = new List<AchievementDetail> { Row("s1", "Saren") };

            Assert.AreEqual(1, MetadataOverlayRun.MatchByList(targets, sources, new[] { new MetadataSourceList("a", "A", sources) }, false).Count);
            Assert.AreEqual(1, MetadataOverlayRun.MatchByList(targets, sources, null, false).Count);
        }

        [TestMethod]
        public void MatchByList_UsesEachSourceRowOnce()
        {
            var targets = new List<AchievementDetail> { Row("t1", "Undefeated", "Game 1"), Row("t2", "Undefeated", "Game 2") };
            var only = new List<AchievementDetail> { Row("s1", "Undefeated") };
            var lists = new[] { new MetadataSourceList("a", "A", only), new MetadataSourceList("b", "B", new List<AchievementDetail>()) };

            var matches = MetadataOverlayRun.MatchByList(targets, only, lists, false);

            Assert.AreEqual(1, matches.Count);
        }

        private static AchievementSourceSet Set(string id, params AchievementDetail[] rows) =>
            new AchievementSourceSet("Source", id, rows.ToList());

        private static MetadataLinkRequest Request() =>
            new MetadataLinkRequest(new Playnite.SDK.Models.Game("Game"), new GameAchievementData { GameName = "Game" });

        private sealed class FakeDetails : IMetadataLinkDetails
        {
            public Dictionary<string, string> Described { get; } = new Dictionary<string, string>();

            public string DescribeLink(Guid gameId, string linkId) => Described.TryGetValue(linkId, out var text) ? text : null;

            public string ShownLink(string linkId) => linkId.StartsWith("id:") ? linkId.Substring(3) : linkId;
        }

        private sealed class FakePages : IMetadataPageLinks
        {
            public IReadOnlyList<MetadataPageLink> PageLinks(Guid gameId, string linkId, string servicingProviderKey) =>
                new[] { new MetadataPageLink(linkId, "https://example.test/" + linkId) };
        }

        private sealed class FakeSource : IMetadataProvider
        {
            public Dictionary<string, List<AchievementDetail>> Rows { get; } = new Dictionary<string, List<AchievementDetail>>();

            public Dictionary<string, string> Titles { get; } = new Dictionary<string, string>();

            public List<string> Fetched { get; } = new List<string>();

            public string Key => "Source";

            public string DisplayNameKey => "Source";

            public MetadataField SupportedFields => MetadataField.Icons;

            public bool IsNativeFor(string servicingProviderKey) => false;

            public Task<bool> BeginRunAsync(CancellationToken ct) => Task.FromResult(true);

            public void EndRun()
            {
            }

            public Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct) =>
                Task.FromResult<MetadataSourceLink>(null);

            public Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
            {
                Fetched.Add(linkId);
                if (!Rows.TryGetValue(linkId, out var rows))
                {
                    return Task.FromResult<AchievementSourceSet>(null);
                }

                return Task.FromResult(new AchievementSourceSet(Key, linkId, rows.ToList())
                {
                    Title = Titles.TryGetValue(linkId, out var title) ? title : null
                });
            }
        }
    }
}
