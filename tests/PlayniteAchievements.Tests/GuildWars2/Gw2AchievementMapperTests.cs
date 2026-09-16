using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.GuildWars2;

namespace PlayniteAchievements.GuildWars2.Tests
{
    [TestClass]
    public class Gw2AchievementMapperTests
    {
        /// <summary>
        /// Mirrors the real /v2/achievements shape. 1 is a four-tier ladder, 2 a single-tier
        /// achievement with its own icon, 3 is hidden, 4 is listed by two categories, and 5 is
        /// reachable only through the Historical group.
        /// </summary>
        private const string AchievementsJson = @"[
            {
                ""id"": 1, ""name"": ""Slayer"", ""description"": ""Nothing but corpses in your wake."",
                ""requirement"": ""Kill foes."", ""locked_text"": """", ""type"": ""Default"",
                ""flags"": [""Permanent""],
                ""tiers"": [
                    { ""count"": 10, ""points"": 1 }, { ""count"": 50, ""points"": 2 },
                    { ""count"": 100, ""points"": 3 }, { ""count"": 1000, ""points"": 5 }
                ]
            },
            {
                ""id"": 2, ""name"": ""Explorer"", ""description"": """",
                ""requirement"": ""Discover every area."", ""type"": ""Default"",
                ""icon"": ""https://render.guildwars2.com/file/ABC/1.png"",
                ""flags"": [""Permanent""],
                ""tiers"": [ { ""count"": 1, ""points"": 10 } ]
            },
            {
                ""id"": 3, ""name"": ""Secret"", ""description"": ""A hidden deed."",
                ""requirement"": """", ""type"": ""Default"",
                ""flags"": [""Permanent"", ""Hidden""],
                ""tiers"": [ { ""count"": 1, ""points"": 5 } ]
            },
            {
                ""id"": 4, ""name"": ""Shared"", ""description"": ""Listed twice."",
                ""requirement"": """", ""type"": ""Default"", ""flags"": [""Permanent""],
                ""tiers"": [ { ""count"": 1, ""points"": 1 } ]
            },
            {
                ""id"": 5, ""name"": ""Retired"", ""description"": ""No longer available."",
                ""requirement"": """", ""type"": ""Default"", ""flags"": [""Permanent""],
                ""tiers"": [ { ""count"": 1, ""points"": 1 } ]
            }
        ]";

        private const string CategoriesJson = @"[
            { ""id"": 10, ""name"": ""Slayer"", ""order"": 1,
              ""icon"": ""https://render.guildwars2.com/file/CAT10/1.png"",
              ""achievements"": [1, 2, 4] },
            { ""id"": 11, ""name"": ""Secrets"", ""order"": 2,
              ""icon"": ""https://render.guildwars2.com/file/CAT11/1.png"",
              ""achievements"": [3, 4] },
            { ""id"": 90, ""name"": ""Old Events"", ""order"": 1,
              ""icon"": ""https://render.guildwars2.com/file/CAT90/1.png"",
              ""achievements"": [5] }
        ]";

        /// <summary>Group 2 is the real Historical GUID, which drives the Missable classification.</summary>
        private const string GroupsJson = @"[
            { ""id"": ""11111111-1111-1111-1111-111111111111"", ""name"": ""General"", ""order"": 6,
              ""categories"": [10, 11] },
            { ""id"": ""A9F7378E-9C8A-48CC-9505-3094E661D5F6"", ""name"": ""Historical"", ""order"": 99,
              ""categories"": [90] }
        ]";

        private static Gw2Catalog BuildCatalog()
        {
            return new Gw2Catalog
            {
                BuildId = 205780,
                Language = "en",
                Groups = JsonConvert.DeserializeObject<List<Gw2Group>>(GroupsJson),
                Categories = JsonConvert.DeserializeObject<List<Gw2Category>>(CategoriesJson),
                Achievements = JsonConvert.DeserializeObject<List<Gw2Achievement>>(AchievementsJson)
            };
        }

        private static List<AchievementDetail> Build(string accountJson = "[]")
        {
            var account = JsonConvert.DeserializeObject<List<Gw2AccountAchievement>>(accountJson);
            var progress = Gw2AchievementMapper.BuildProgressIndex(account);
            return Gw2AchievementMapper.BuildAchievements(BuildCatalog(), progress);
        }

        private static AchievementDetail Find(IEnumerable<AchievementDetail> all, string apiName)
            => all.Single(a => a.ApiName == apiName);

        [TestMethod]
        public void BuildAchievements_EmitsOneRowPerTier()
        {
            var results = Build();

            // Four tiers for id 1, plus one each for ids 2-5.
            Assert.AreEqual(8, results.Count);
            Assert.AreEqual(4, results.Count(a => a.ApiName.StartsWith("1:t")));
        }

        [TestMethod]
        public void BuildAchievements_KeysTiersByIndexNotThreshold()
        {
            var results = Build();

            CollectionAssert.AreEqual(
                new[] { "1:t1", "1:t2", "1:t3", "1:t4" },
                results.Where(a => a.ApiName.StartsWith("1:t")).Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void BuildAchievements_SuffixesMultiTierNamesWithTheThreshold()
        {
            var results = Build();

            // Every tier shares one icon, so the threshold is the only thing telling the rows apart.
            Assert.AreEqual("Slayer (10)", Find(results, "1:t1").DisplayName);
            Assert.AreEqual("Slayer (1,000)", Find(results, "1:t4").DisplayName);
        }

        [TestMethod]
        public void BuildAchievements_LeavesSingleTierNamesBare()
        {
            Assert.AreEqual("Explorer", Find(Build(), "2:t1").DisplayName);
        }

        [TestMethod]
        public void BuildAchievements_AbsentFromAccountMeansLocked()
        {
            var results = Build();

            Assert.IsTrue(results.All(a => !a.Unlocked));
            Assert.AreEqual(0, Find(results, "1:t1").ProgressNum);
        }

        [TestMethod]
        public void BuildAchievements_UnlocksTiersBeneathTheRunningTotal()
        {
            var results = Build(@"[ { ""id"": 1, ""current"": 60, ""max"": 100, ""done"": false } ]");

            Assert.IsTrue(Find(results, "1:t1").Unlocked, "60 is past the 10 threshold.");
            Assert.IsTrue(Find(results, "1:t2").Unlocked, "60 is past the 50 threshold.");
            Assert.IsFalse(Find(results, "1:t3").Unlocked, "60 has not reached 100.");
            Assert.IsFalse(Find(results, "1:t4").Unlocked);
        }

        [TestMethod]
        public void BuildAchievements_MeasuresProgressAgainstEachTiersOwnThreshold()
        {
            var results = Build(@"[ { ""id"": 1, ""current"": 60, ""max"": 100, ""done"": false } ]");

            // An earned tier reads full; the tiers above it show how far the same running value has come.
            Assert.AreEqual(10, Find(results, "1:t1").ProgressNum);
            Assert.AreEqual(10, Find(results, "1:t1").ProgressDenom);

            Assert.AreEqual(60, Find(results, "1:t3").ProgressNum);
            Assert.AreEqual(100, Find(results, "1:t3").ProgressDenom);
        }

        [TestMethod]
        public void BuildAchievements_DoneUnlocksEveryTier()
        {
            // A finished ladder stops reporting a running total, so "done" is the only signal left.
            var results = Build(@"[ { ""id"": 1, ""done"": true } ]");

            Assert.IsTrue(results.Where(a => a.ApiName.StartsWith("1:t")).All(a => a.Unlocked));
        }

        [TestMethod]
        public void BuildAchievements_RepeatedUnlocksEveryTier()
        {
            // A repeatable achievement that has looped reports done=false with a low current.
            var results = Build(@"[ { ""id"": 1, ""current"": 3, ""max"": 10, ""done"": false, ""repeated"": 2 } ]");

            Assert.IsTrue(results.Where(a => a.ApiName.StartsWith("1:t")).All(a => a.Unlocked));
        }

        [TestMethod]
        public void BuildAchievements_NeverReportsAnUnlockTime()
        {
            var results = Build(@"[ { ""id"": 1, ""done"": true }, { ""id"": 2, ""done"": true } ]");

            // The API records none, and an invented one would be a lie the unlock feed orders by.
            Assert.IsTrue(results.All(a => a.UnlockTimeUtc == null));
            Assert.IsTrue(results.Any(a => a.Unlocked), "guard: the fixture must actually unlock something");
        }

        [TestMethod]
        public void BuildAchievements_NeverReportsRarity()
        {
            var results = Build(@"[ { ""id"": 1, ""done"": true } ]");

            // A null percent plus the default tier is how the cache layer recognizes "unknown" and
            // declines to overwrite a stored value.
            Assert.IsTrue(results.All(a => a.GlobalPercentUnlocked == null));
            Assert.IsTrue(results.All(a => a.Rarity == RarityTier.Common));
        }

        [TestMethod]
        public void BuildAchievements_BuildsATwoLevelCategoryPath()
        {
            var results = Build();

            // Category names are not unique across groups, so the group has to be part of the path.
            var slayer = Find(results, "1:t1");
            StringAssert.Contains(slayer.Category, "General");
            StringAssert.Contains(slayer.Category, "Slayer");
        }

        [TestMethod]
        public void BuildAchievements_ClassifiesTheHistoricalGroupAsMissable()
        {
            var results = Build();

            Assert.AreEqual("Missable", Find(results, "5:t1").CategoryType);
            Assert.IsNull(Find(results, "1:t1").CategoryType, "an ordinary group carries no classification");
        }

        [TestMethod]
        public void BuildAchievements_FallsBackToTheCategoryIconWhenTheAchievementHasNone()
        {
            var results = Build();

            // Only about 9% of achievements carry their own art.
            Assert.AreEqual("https://render.guildwars2.com/file/ABC/1.png", Find(results, "2:t1").UnlockedIconPath);
            Assert.AreEqual("https://render.guildwars2.com/file/CAT10/1.png", Find(results, "1:t1").UnlockedIconPath);
        }

        [TestMethod]
        public void BuildAchievements_CarriesTheHiddenFlag()
        {
            var results = Build();

            Assert.IsTrue(Find(results, "3:t1").Hidden);
            Assert.IsFalse(Find(results, "1:t1").Hidden);
        }

        [TestMethod]
        public void BuildAchievements_AwardsEachTierItsOwnPoints()
        {
            var results = Build();

            CollectionAssert.AreEqual(
                new int?[] { 1, 2, 3, 5 },
                results.Where(a => a.ApiName.StartsWith("1:t")).Select(a => a.Points).ToArray());
        }

        [TestMethod]
        public void BuildAchievements_ClaimsAnAchievementListedTwiceOnlyOnce()
        {
            var results = Build();

            // Id 4 appears under both categories; the first in display order wins, so its identity
            // and category path stay stable between refreshes.
            Assert.AreEqual(1, results.Count(a => a.ApiName == "4:t1"));
            StringAssert.Contains(Find(results, "4:t1").Category, "Slayer");
        }

        [TestMethod]
        public void BuildAchievements_PrefersDescriptionAndFallsBackToRequirement()
        {
            var results = Build();

            Assert.AreEqual("Nothing but corpses in your wake.", Find(results, "1:t1").Description);
            Assert.AreEqual("Discover every area.", Find(results, "2:t1").Description);
        }

        [TestMethod]
        public void BuildAchievements_OrdersByGroupThenCategoryThenTier()
        {
            var results = Build();

            // Historical sorts last on its order of 99, so its achievement trails the rest.
            Assert.AreEqual("5:t1", results.Last().ApiName);
            Assert.AreEqual("1:t1", results.First().ApiName);
        }

        [TestMethod]
        public void BuildAchievements_ReturnsEmptyForAnUnusableCatalog()
        {
            Assert.AreEqual(0, Gw2AchievementMapper.BuildAchievements(null, null).Count);
            Assert.AreEqual(0, Gw2AchievementMapper.BuildAchievements(new Gw2Catalog(), null).Count);
        }

        [TestMethod]
        public void BuildAchievements_ToleratesANullProgressIndex()
        {
            var results = Gw2AchievementMapper.BuildAchievements(BuildCatalog(), null);

            Assert.AreEqual(8, results.Count);
            Assert.IsTrue(results.All(a => !a.Unlocked));
        }

        /// <summary>
        /// The example response published for /v2/account/achievements, copied verbatim. Nothing in
        /// this suite holds a real API key, so this is what pins the DTO's field names to the
        /// documented contract: a typo in a JsonProperty would otherwise read as silent zeros and
        /// every achievement would simply look locked.
        /// </summary>
        private const string DocumentedAccountJson = @"[
            { ""id"": 1, ""current"": 1, ""max"": 1000, ""done"": false },
            { ""id"": 202, ""done"": true },
            { ""id"": 1653, ""bits"": [2, 3, 4, 5], ""current"": 4, ""max"": 30, ""done"": false }
        ]";

        [TestMethod]
        public void AccountAchievement_ParsesTheDocumentedPayloadShape()
        {
            var parsed = JsonConvert.DeserializeObject<List<Gw2AccountAchievement>>(DocumentedAccountJson);

            Assert.AreEqual(3, parsed.Count);

            var partial = parsed[0];
            Assert.AreEqual(1, partial.Id);
            Assert.AreEqual(1, partial.Current);
            Assert.AreEqual(1000, partial.Max);
            Assert.IsFalse(partial.Done);

            // A finished achievement carries neither a running total nor a max.
            var finished = parsed[1];
            Assert.AreEqual(202, finished.Id);
            Assert.IsTrue(finished.Done);
            Assert.IsNull(finished.Current);
            Assert.IsNull(finished.Max);

            var checklist = parsed[2];
            Assert.AreEqual(4, checklist.Current);
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, checklist.Bits);

            // "unlocked" is absent throughout, which the API defines as unlocked.
            Assert.IsTrue(parsed.All(a => a.Unlocked == null));
        }

        [TestMethod]
        public void BuildProgressIndex_KeysTheDocumentedPayloadById()
        {
            var index = Gw2AchievementMapper.BuildProgressIndex(
                JsonConvert.DeserializeObject<List<Gw2AccountAchievement>>(DocumentedAccountJson));

            Assert.AreEqual(3, index.Count);
            Assert.IsTrue(index[202].Done);
            Assert.AreEqual(1, index[1].Current);
        }

        [TestMethod]
        public void BuildAchievements_TreatsADoneEntryWithNoRunningTotalAsFullyEarned()
        {
            // The shape the documented payload gives for id 202: done, with current and max absent.
            // Reading the missing current as 0 would leave every tier locked.
            var results = Build(@"[ { ""id"": 1, ""done"": true } ]");
            var tiers = results.Where(a => a.ApiName.StartsWith("1:t")).ToList();

            Assert.IsTrue(tiers.All(a => a.Unlocked));
            Assert.AreEqual(0, tiers[0].ProgressNum, "progress still reads from the absent total");
        }

        [TestMethod]
        public void CollectAchievementIds_DeduplicatesAndFollowsCategoryOrder()
        {
            var categories = JsonConvert.DeserializeObject<List<Gw2Category>>(CategoriesJson);

            var ids = Gw2AchievementMapper.CollectAchievementIds(categories);

            // 4 is listed by two categories but paged once; 90 and 10 share an order of 1.
            CollectionAssert.AreEquivalent(new[] { 1, 2, 4, 5, 3 }, ids);
            Assert.AreEqual(5, ids.Count);
        }

        [TestMethod]
        public void CollectAchievementIds_ToleratesNullsAndSkipsNonPositiveIds()
        {
            var categories = new List<Gw2Category>
            {
                null,
                new Gw2Category { Id = 1, Order = 1, Achievements = null },
                new Gw2Category { Id = 2, Order = 2, Achievements = new List<int> { 0, -1, 7 } }
            };

            CollectionAssert.AreEqual(new[] { 7 }, Gw2AchievementMapper.CollectAchievementIds(categories));
            Assert.AreEqual(0, Gw2AchievementMapper.CollectAchievementIds(null).Count);
        }
    }
}
