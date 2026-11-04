using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Metadata;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CrossSourceAchievementMatcherTests
    {
        [TestMethod]
        public void UniqueTitle_MatchesIgnoringCaseAndPunctuation()
        {
            var target = Achievement("epic_1", "Dragon-Slayer!");
            var source = Achievement("steam_1", "dragon slayer");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source });

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(target, matches[0].Target);
            Assert.AreSame(source, matches[0].Source);
        }

        [TestMethod]
        public void UniqueTitle_MatchesIgnoringAccents()
        {
            var target = Achievement("xbox_1", "Pokemon Master of Ragnarok");
            var source = Achievement("NPWR:1", "Pokémon Master of Ragnarök");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source });

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(source, matches[0].Source);
        }

        [TestMethod]
        public void NormalizeText_DropsApostrophes()
        {
            Assert.AreEqual("dont stop", CrossSourceAchievementMatcher.NormalizeText("Don't Stop"));
            Assert.AreEqual("dont stop", CrossSourceAchievementMatcher.NormalizeText("Don’t stop!"));
            Assert.AreEqual("dont stop", CrossSourceAchievementMatcher.NormalizeText("Dont Stop"));
        }

        [TestMethod]
        public void NormalizeText_FoldsBritishAndAmericanSpellings()
        {
            Assert.AreEqual(
                CrossSourceAchievementMatcher.NormalizeText("Dragonslayer Armor"),
                CrossSourceAchievementMatcher.NormalizeText("Dragonslayer Armour"));
            Assert.AreEqual(
                CrossSourceAchievementMatcher.NormalizeText("Realize your Organization"),
                CrossSourceAchievementMatcher.NormalizeText("Realise your Organisation"));
            Assert.AreEqual(
                CrossSourceAchievementMatcher.NormalizeText("Analyze This"),
                CrossSourceAchievementMatcher.NormalizeText("Analyse This"));
        }

        [TestMethod]
        public void BritishSpelling_StillMatchesByTitle()
        {
            var target = Achievement("ACH_ARMOUR", "Dragonslayer Armour");
            var source = Achievement("p1", "Dragonslayer Armor");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source });

            Assert.AreEqual(1, matches.Count);
        }

        [TestMethod]
        public void NormalizeText_KeepsMarksThatChangeJapaneseLetters()
        {
            Assert.AreNotEqual(
                CrossSourceAchievementMatcher.NormalizeText("が"),
                CrossSourceAchievementMatcher.NormalizeText("か"));
            Assert.AreEqual("cafe", CrossSourceAchievementMatcher.NormalizeText("Café"));
            // Russian text often writes ё as е.
            Assert.AreEqual("ежик", CrossSourceAchievementMatcher.NormalizeText("Ёжик"));
        }

        [TestMethod]
        public void DuplicateTitles_AreSeparatedByDescription()
        {
            var first = Achievement("a", "Collector", "Find all coins");
            var second = Achievement("b", "Collector", "Find all gems");
            var gems = Achievement("x", "Collector", "find all gems.");
            var coins = Achievement("y", "Collector", "Find all coins");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { gems, coins });

            Assert.AreSame(coins, matches.Single(m => m.Target == first).Source);
            Assert.AreSame(gems, matches.Single(m => m.Target == second).Source);
        }

        [TestMethod]
        public void TiedScores_LeaveTheAchievementUnmatched()
        {
            var first = Achievement("a", "Collector");
            var second = Achievement("b", "Collector");
            var source1 = Achievement("x", "Collector");
            var source2 = Achievement("y", "Collector");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { source1, source2 });

            Assert.AreEqual(0, matches.Count);
        }

        [TestMethod]
        public void SourceRow_IsUsedOnlyOnce()
        {
            var first = Achievement("a", "Collector", "Find all coins");
            var second = Achievement("b", "Collector", "Find all coins");
            var source = Achievement("x", "Collector", "Find all coins");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { source });

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(first, matches[0].Target);
        }

        [TestMethod]
        public void RepeatedFirstWord_WithoutSeparator_MatchesASeparatedTag()
        {
            var targets = new[]
            {
                Achievement("1", "TR1 | The Unfound Tomb of Qualopec"),
                Achievement("2", "TR1 | Look Over Us Kindly, Tihocan"),
                Achievement("3", "TR1 | The Cataclysm of Atlantis")
            };
            var sources = new[]
            {
                Achievement("x", "TR The Cataclysm of Atlantis"),
                Achievement("y", "TR The Unfound Tomb of Qualopec"),
                Achievement("z", "TR Look Over Us Kindly Tihocan")
            };

            var matches = CrossSourceAchievementMatcher.Match(targets, sources);

            Assert.AreEqual(3, matches.Count);
            Assert.AreSame(sources[0], matches.Single(match => match.Target == targets[2]).Source);
        }

        [TestMethod]
        public void SeparatedTag_MeetsATitleWithoutItsFirstWord()
        {
            // An expansion's tag ("TR:UB |") against the site's bare game tag ("TR").
            var targets = new[]
            {
                Achievement("1", "TR:UB | Miu"),
                Achievement("2", "TR:UB | Business Is Finished"),
                Achievement("3", "TR:UB | Codex of Cat")
            };
            var sources = new[]
            {
                Achievement("x", "TR Codex of Cat"),
                Achievement("y", "TR Miu"),
                Achievement("z", "TR Business Is Finished")
            };

            var matches = CrossSourceAchievementMatcher.Match(targets, sources);

            Assert.AreEqual(3, matches.Count);
            Assert.AreSame(sources[1], matches.Single(match => match.Target == targets[0]).Source);
        }

        [TestMethod]
        public void FirstWordPass_LeavesEarlierMatchesAlone()
        {
            // "The" starts three source titles; the plain title pass already pairs "The End".
            var end = Achievement("1", "The End");
            var other = Achievement("2", "Beginning");
            var sources = new[]
            {
                Achievement("x", "The End"),
                Achievement("y", "The Beginning"),
                Achievement("z", "The Middle")
            };

            var matches = CrossSourceAchievementMatcher.Match(new[] { end, other }, sources);

            Assert.AreSame(sources[0], matches.Single(match => match.Target == end).Source);
            Assert.AreSame(sources[1], matches.Single(match => match.Target == other).Source);
        }

        [TestMethod]
        public void UnrepeatedFirstWord_IsKept()
        {
            var target = Achievement("1", "Collector");
            var source = Achievement("x", "Gold Collector");

            Assert.AreEqual(0, CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source }).Count);
        }

        [TestMethod]
        public void RenamedTitle_PairsByAUniqueDescription()
        {
            var target = Achievement("1", "Dark Boy", "Beat The Lamb as Azazel");
            var source = Achievement("x", "Dark Boy!", "Beat The Lamb as Azazel");
            var renamed = Achievement("2", "Il Lago", "Defeat the boss of the lake");
            var renamedSource = Achievement("y", "The Lake", "Defeat the boss of the lake");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target, renamed }, new[] { renamedSource, source });

            Assert.AreSame(renamedSource, matches.Single(match => match.Target == renamed).Source);
        }

        [TestMethod]
        public void RepeatedDescription_NeverPairsOnItsOwn()
        {
            var targets = new[]
            {
                Achievement("1", "Isaac's Head", "Beat Mom's Heart 11 times"),
                Achievement("2", "Maggy's Faith", "Beat Mom's Heart 11 times")
            };
            var sources = new[]
            {
                Achievement("x", "Cain's Eye", "Beat Mom's Heart 11 times"),
                Achievement("y", "Judas' Tongue", "Beat Mom's Heart 11 times")
            };

            Assert.AreEqual(0, CrossSourceAchievementMatcher.Match(targets, sources).Count);
        }

        [TestMethod]
        public void ShortDescription_NeverPairsOnItsOwn()
        {
            var target = Achievement("1", "Winner", "Win.");
            var source = Achievement("x", "Victor", "Win.");

            Assert.AreEqual(0, CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source }).Count);
        }

        [TestMethod]
        public void DuplicateTitles_AreSeparatedByASimilarDescription()
        {
            var first = Achievement("a", "Collector", "Find all the coins in the first world");
            var second = Achievement("b", "Collector", "Find all the coins in the second world");
            var sourceFirst = Achievement("x", "Collector", "Collect every coin in the first world");
            var sourceSecond = Achievement("y", "Collector", "Collect every coin in the second world");

            var matches = CrossSourceAchievementMatcher.Match(new[] { first, second }, new[] { sourceSecond, sourceFirst });

            Assert.AreSame(sourceFirst, matches.Single(match => match.Target == first).Source);
            Assert.AreSame(sourceSecond, matches.Single(match => match.Target == second).Source);
        }

        [TestMethod]
        public void DescriptionSimilarity_IsSharedWordsOverAllWords()
        {
            Assert.AreEqual(1, CrossSourceAchievementMatcher.DescriptionSimilarity("Beat the boss.", "beat the BOSS"));
            Assert.AreEqual(0.5, CrossSourceAchievementMatcher.DescriptionSimilarity("beat the boss", "beat the lamb"), 1e-9);
            Assert.AreEqual(0, CrossSourceAchievementMatcher.DescriptionSimilarity(null, "beat the boss"));
        }

        [TestMethod]
        public void SourceFilter_ExcludesRows()
        {
            var target = Achievement("a", "Collector");
            var source = Achievement("x", "Collector");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { source }, row => false);

            Assert.AreEqual(0, matches.Count);
        }

        [TestMethod]
        public void ApiNamePass_WinsOverTitle_WhenEnabled()
        {
            var target = Achievement("ACH_WIN", "Victory");
            var renamed = Achievement("ach_win", "Triumph");
            var sameTitle = Achievement("OTHER", "Victory");

            var matches = CrossSourceAchievementMatcher.Match(
                new[] { target },
                new[] { sameTitle, renamed },
                matchApiNames: true);

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(renamed, matches[0].Source);
        }

        [TestMethod]
        public void ApiNamePass_IsOffByDefault()
        {
            var target = Achievement("ACH_WIN", "Victory");
            var renamed = Achievement("ACH_WIN", "Triumph");

            var matches = CrossSourceAchievementMatcher.Match(new[] { target }, new[] { renamed });

            Assert.AreEqual(0, matches.Count);
        }

        [TestMethod]
        public void ApiNameMatchedTargets_DoNotCountTowardTitleDuplicates()
        {
            // Once "a" is joined by ApiName, "b" is the only remaining "Collector" and pairs 1:1.
            var first = Achievement("a", "Collector");
            var second = Achievement("b", "Collector");
            var byApi = Achievement("a", "Something else");
            var byTitle = Achievement("z", "Collector");

            var matches = CrossSourceAchievementMatcher.Match(
                new[] { first, second },
                new[] { byApi, byTitle },
                matchApiNames: true);

            Assert.AreEqual(2, matches.Count);
            Assert.AreSame(byTitle, matches.Single(m => m.Target == second).Source);
        }

        [TestMethod]
        public void ApiNameOverlap_CountsOnlyIdsWithLetters()
        {
            var targets = new[] { Achievement("ACH_A", "x"), Achievement("ACH_B", "y"), Achievement("1", "z"), Achievement("2", "w") };
            var sources = new[] { Achievement("ach_a", "a"), Achievement("1", "b"), Achievement("2", "c") };

            // Of the two distinctive target IDs, one appears; the numeric IDs are ignored.
            Assert.AreEqual(0.5, CrossSourceAchievementMatcher.ApiNameOverlap(targets, sources), 0.0001);
        }

        [TestMethod]
        public void ApiNameOverlap_IsZeroForNumericOnlyIds()
        {
            var targets = new[] { Achievement("1", "x"), Achievement("2", "y") };
            var sources = new[] { Achievement("1", "a"), Achievement("2", "b") };

            Assert.AreEqual(0, CrossSourceAchievementMatcher.ApiNameOverlap(targets, sources));
        }

        private static AchievementDetail Achievement(string apiName, string title, string description = null)
        {
            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = title,
                Description = description
            };
        }
    }
}
