using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Providers.Metadata.TrueGaming;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    [TestCategory("Providers")]
    public class TrueGamingPageSetTests
    {
        private static readonly string[] Targets = { "Lumière", "Spring Meadows", "The End" };

        [TestMethod]
        public void Tags_NeedNoMorePages_OnceEveryAchievementIsListedWithFlags()
        {
            var full = Page(Row("a1", "Lumière", "Single Player"), Row("a2", "Spring Meadows", "Missable"), Row("a3", "The End", "Story Completed"));
            var unflagged = Page(Row("a1", "Lumière", "Single Player"), Row("a2", "Spring Meadows"), Row("a3", "The End", "Story Completed"));
            var partial = Page(Row("a1", "Lumière", "Single Player"), Row("a2", "Spring Meadows", "Missable"));
            var wanted = MetadataField.Tags;

            Assert.IsFalse(TrueGamingPageSet.NeedsPage(TrueGamingSite.TrueTrophies, wanted, new[] { full }, Targets));
            Assert.IsTrue(TrueGamingPageSet.NeedsPage(TrueGamingSite.TrueTrophies, wanted, new[] { unflagged }, Targets), "a row without flags");
            Assert.IsTrue(TrueGamingPageSet.NeedsPage(TrueGamingSite.TrueTrophies, wanted, new[] { partial }, Targets), "a missing row");
            Assert.IsTrue(TrueGamingPageSet.NeedsPage(TrueGamingSite.TrueAchievements, wanted, new TrueGamingPage[0], Targets));
            Assert.IsFalse(TrueGamingPageSet.NeedsPage(TrueGamingSite.TrueAchievements, MetadataField.Categories, new TrueGamingPage[0], Targets), "only tags need a page");
        }

        [TestMethod]
        public void Combine_TakesEachFieldFromItsPage()
        {
            var ta = Page(Row("a1", "Lumière", "Single Player"), Row("a2", "Spring Meadows"));
            ta.Achievements[0].Points = 10;
            ta.Achievements[1].Points = 20;
            var tt = Page(Row("t1", "Lumiere", "Missable"), Row("t2", "Spring Meadows", "Collectable"), Row("t3", "The End", "Story Completed"));
            tt.Achievements[0].TrophyType = "bronze";
            tt.Achievements[2].TrophyType = "gold";
            tt.Achievements[2].Dlc = "Epilogue";

            var rows = TrueGamingPageSet.Combine(
                new[]
                {
                    new TrueGamingFoundPage(TrueGamingSite.TrueAchievements, "Clair", ta),
                    new TrueGamingFoundPage(TrueGamingSite.TrueTrophies, "Clair", tt)
                },
                "Clair Obscur");

            Assert.AreEqual(3, rows.Count, "a row only the second page lists is added");
            var lumiere = rows[0];
            Assert.AreEqual("a1", lumiere.ApiName, "the first page's row names it");
            Assert.AreEqual("Singleplayer", lumiere.Tags, "tags from the first page that flags it");
            Assert.AreEqual(10, lumiere.Points);
            Assert.AreEqual("bronze", lumiere.TrophyType);
            Assert.AreEqual("Clair Obscur", lumiere.Category);
            Assert.AreEqual("Collectable", rows[1].Tags, "an unflagged row takes the next page's flags");
            Assert.AreEqual("Epilogue", rows[2].Category);
            Assert.IsNull(rows[2].Points, "points come from TrueAchievements only");
        }

        [TestMethod]
        public void Combine_JoinsARowTheSitesNameDifferently_ByItsDescription()
        {
            var ta = Page(Described("a1", "Grilled Tastes Better", "Defeat Miltos"), Described("a2", "Free at Last", "Defeat the Black Warden"));
            ta.Achievements[0].Points = 15;
            ta.Achievements[1].Points = 30;
            var tt = Page(Described("t1", "Grilled Tastes Better", "Defeat Miltos"), Described("t2", "Free at Last", "Defeat the Black Warden"));
            tt.Achievements[0].TrophyType = "bronze";
            var tsa = Page(Described("s1", "Grilled Tasty", "Defeat Miltos"), Described("s2", "Finally Free", "Defeat the black warden."));

            var combined = TrueGamingPageSet.CombineAll(Pages(ta, tt, tsa), "Asterigos");

            Assert.AreEqual(2, combined.Rows.Count, "the Steam names join the rows the other sites name");
            Assert.AreEqual(2, combined.MergedByDescription);
            Assert.AreEqual("a1", combined.Rows[0].ApiName);
            Assert.AreEqual(15, combined.Rows[0].Points);
            Assert.AreEqual("bronze", combined.Rows[0].TrophyType);
            Assert.AreSame(combined.Rows[0], combined.Aliases["s1"]);
            Assert.AreSame(combined.Rows[0], combined.Aliases["t1"]);
            Assert.AreSame(combined.Rows[1], combined.Aliases["s2"], "descriptions compare by letters and digits");
        }

        [TestMethod]
        public void Combine_KeepsRowsApart_WhenTheDescriptionIsNotOneOfAKind_OrTheDlcDiffers()
        {
            var ta = Page(
                Described("a1", "Chapter One", "Complete a chapter"),
                Described("a2", "Chapter Two", "Complete a chapter"),
                Described("a3", "Hunter", "Defeat the boss"));
            ta.Achievements[2].Dlc = "Expansion";
            var tsa = Page(Described("s1", "First Chapter", "Complete a chapter"), Described("s3", "Boss Hunter", "Defeat the boss"));

            var combined = TrueGamingPageSet.CombineAll(Pages(ta, tsa), "Base");

            Assert.AreEqual(5, combined.Rows.Count);
            Assert.AreEqual(0, combined.MergedByDescription);
            Assert.AreEqual(0, combined.Aliases.Count);
        }

        [TestMethod]
        public void Combine_JoinsARowNamedAndDescribedDifferently_ByItsPlaceOnPagesInTheSameOrder()
        {
            var ta = Page(
                Described("a1", "Your Name?", "Enjoyed a fulfilling chat with Kagami"),
                Described("a2", "The Dream Invader", "Saw Gray twice in Somnium"),
                Described("a3", "Protect Your Head", "Had Aiba wear the basket"));
            ta.Achievements[1].Points = 10;
            var tt = Page(
                Described("t1", "Your Name?", "Enjoyed a fulfilling chat with Kagami"),
                Described("t2", "The Dream Wanderer", "Saw This Man twice in Somnium"),
                Described("t3", "Protect Your Head", "Had Aiba wear the basket"));
            var tsa = Page(
                Described("s1", "Your Name?", "Enjoyed a fulfilling chat with Kagami"),
                Described("s2", "The Dream Wanderer", "Saw This Man twice in Somnium"),
                Described("s3", "Protect Your Head", "Had Aiba wear the basket"));

            var combined = TrueGamingPageSet.CombineAll(Pages(ta, tt, tsa), "Somnium");

            Assert.AreEqual(3, combined.Rows.Count);
            Assert.AreEqual(1, combined.MergedByPosition, "the third page joins by name");
            Assert.AreEqual("a2", combined.Rows[1].ApiName);
            Assert.AreEqual(10, combined.Rows[1].Points);
            Assert.AreSame(combined.Rows[1], combined.Aliases["t2"]);
            Assert.AreSame(combined.Rows[1], combined.Aliases["s2"]);
        }

        [TestMethod]
        public void Combine_KeepsLeftoversApart_WhenThePagesDifferInLengthOrOrder()
        {
            var ta = Page(Described("a1", "First", "One"), Described("a2", "Invader", "Gray"), Described("a3", "Third", "Three"));
            var longer = Page(Described("t1", "First", "One"), Described("t2", "Wanderer", "Man"), Described("t3", "Third", "Three"), Described("t4", "Platinum", "All"));
            var reordered = Page(Described("t3", "Third", "Three"), Described("t2", "Wanderer", "Man"), Described("t1", "First", "One"));
            var otherDlc = Page(Described("t1", "First", "One"), Described("t2", "Wanderer", "Man"), Described("t3", "Third", "Three"));
            otherDlc.Achievements[1].Dlc = "Expansion";

            Assert.AreEqual(0, TrueGamingPageSet.CombineAll(Pages(ta, longer), "Base").MergedByPosition, "a page lists another achievement");
            Assert.AreEqual(0, TrueGamingPageSet.CombineAll(Pages(ta, reordered), "Base").MergedByPosition, "the shared rows sit elsewhere");
            Assert.AreEqual(4, TrueGamingPageSet.CombineAll(Pages(ta, otherDlc), "Base").Rows.Count, "the DLC differs");
        }

        [TestMethod]
        public void SameGame_NeedsHalfTheShorterListByName()
        {
            Assert.IsTrue(TrueGamingPageSet.IsSameGame(Page(Row("a1", "LUMIÈRE"), Row("a9", "Other")), Targets));
            Assert.IsFalse(TrueGamingPageSet.IsSameGame(Page(Row("a8", "Peaky"), Row("a9", "Blinders"), Row("a1", "Lumière")), Targets));
            Assert.IsFalse(TrueGamingPageSet.IsSameGame(Page(), Targets));
        }

        [TestMethod]
        public void SameGame_PairsTitlesBehindACollectionsCodes()
        {
            var targets = new[] { "TR2 | These Doors", "TR2 | Snow March", "TR2 | Gold Rush" };

            Assert.IsTrue(TrueGamingPageSet.IsSameGame(Page(Row("a1", "These Doors"), Row("a2", "Snow March"), Row("a3", "Gold Rush")), targets));
        }

        private static TrueGamingFoundPage[] Pages(params TrueGamingPage[] pages) =>
            pages.Select((page, index) => new TrueGamingFoundPage(TrueGamingSite.LookupOrder[index], "Game", page)).ToArray();

        private static TrueGamingAchievement Described(string id, string name, string description)
        {
            var row = Row(id, name);
            row.Description = description;
            return row;
        }

        private static TrueGamingPage Page(params TrueGamingAchievement[] rows)
        {
            var page = new TrueGamingPage { FlagsDecoded = true };
            page.Achievements.AddRange(rows);
            return page;
        }

        private static TrueGamingAchievement Row(string id, string name, params string[] flags)
        {
            var row = new TrueGamingAchievement { Id = id, Name = name };
            row.Flags.AddRange(flags);
            return row;
        }
    }
}
