using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    [TestCategory("Services")]
    public class MetadataPageSearchTests
    {
        // Pages by id, and what a search for each name offers (id, title), likeliest first.
        private readonly Dictionary<string, List<AchievementDetail>> _pages = new Dictionary<string, List<AchievementDetail>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<MetadataPageCandidate>> _searches = new Dictionary<string, List<MetadataPageCandidate>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _searched = new List<string>();

        private MetadataPageSearch Search() => new MetadataPageSearch(
            (name, ct) =>
            {
                _searched.Add(name);
                return Task.FromResult<IReadOnlyList<MetadataPageCandidate>>(
                    _searches.TryGetValue(name, out var found) ? found : new List<MetadataPageCandidate>());
            },
            (id, ct) => Task.FromResult<IList<AchievementDetail>>(_pages.TryGetValue(id, out var rows) ? rows : null));

        private void Page(string id, params string[] titles) =>
            _pages[id] = titles.Select((title, i) => Row(id + i, title, null)).ToList();

        private void Offers(string name, params (string Id, string Title)[] pages) =>
            _searches[name] = pages.Select(page => new MetadataPageCandidate(page.Id, page.Title)).ToList();

        private static AchievementDetail Row(string apiName, string title, string category, string kind = CategoryKindHelper.Base) =>
            new AchievementDetail { ApiName = apiName, DisplayName = title, Category = category, CategoryKind = category == null ? null : kind };

        private static List<AchievementDetail> Game(string category, params string[] titles) =>
            titles.Select((title, i) => Row(category + i, title, category)).ToList();

        [TestMethod]
        public async Task PicksThePageThatMatchesMost_NotTheFirstOffered()
        {
            Page("old-release", "Saren", "Spectre");
            Page("remaster", "Saren", "Spectre", "Paragon", "Renegade");
            Offers("Mass Effect", ("old-release", "Mass Effect"), ("remaster", "Legendary Edition: Mass Effect"));
            var targets = Game("Mass Effect", "Saren", "Spectre", "Paragon", "Renegade");

            var choice = await Search().ChooseAsync(new[] { "Mass Effect" }, targets, null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "remaster" }, choice.Ids.ToList());
            Assert.AreEqual(4, choice.Matched);
        }

        [TestMethod]
        public async Task BroaderNames_AskTheVariantOffer_TheFullNameTheUsualOne()
        {
            Page("spyro", "Sheep", "Gems");
            Offers("Spyro", ("spyro", "Spyro"));
            var variantNames = new List<string>();
            var search = new MetadataPageSearch(
                (name, ct) =>
                {
                    _searched.Add(name);
                    return Task.FromResult<IReadOnlyList<MetadataPageCandidate>>(new List<MetadataPageCandidate>());
                },
                (id, ct) => Task.FromResult<IList<AchievementDetail>>(_pages.TryGetValue(id, out var rows) ? rows : null),
                (name, ct) =>
                {
                    variantNames.Add(name);
                    return Task.FromResult<IReadOnlyList<MetadataPageCandidate>>(
                        _searches.TryGetValue(name, out var found) ? found : new List<MetadataPageCandidate>());
                });

            var choice = await search.ChooseAsync(new[] { "Spyro the Dragon" }, Game(null, "Sheep", "Gems"), null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Spyro the Dragon" }, _searched);
            CollectionAssert.AreEqual(new[] { "Spyro the", "Spyro" }, variantNames);
            CollectionAssert.AreEqual(new[] { "spyro" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task Collection_LinksEachGamesPage_WhenTogetherTheyMatchMore()
        {
            Page("launcher", "Bonus");
            Page("me1", "Saren", "Spectre");
            Page("me2", "Suicide Mission", "Collector");
            Offers("Mass Effect Legendary Edition", ("launcher", "Mass Effect Legendary Edition"));
            Offers("Mass Effect 1", ("me1", "Legendary Edition: Mass Effect"));
            Offers("Mass Effect 2", ("me2", "Legendary Edition: Mass Effect 2"));
            var targets = Game("Mass Effect 1", "Saren", "Spectre").Concat(Game("Mass Effect 2", "Suicide Mission", "Collector")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Mass Effect Legendary Edition" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "me1", "me2" }, choice.Ids.ToList());
            Assert.AreEqual(4, choice.Matched);
        }

        [TestMethod]
        public async Task Collection_KeepsOnePage_WhenItMatchesAsMuch()
        {
            Page("whole", "Saren", "Spectre", "Suicide Mission", "Collector");
            Page("me1", "Saren", "Spectre");
            Page("me2", "Suicide Mission", "Collector");
            Offers("Trilogy", ("whole", "Trilogy"));
            Offers("Mass Effect 1", ("me1", "Mass Effect"));
            Offers("Mass Effect 2", ("me2", "Mass Effect 2"));
            var targets = Game("Mass Effect 1", "Saren", "Spectre").Concat(Game("Mass Effect 2", "Suicide Mission", "Collector")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Trilogy" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "whole" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task Collection_NamesAGameLikeItsSibling_WhenItsOwnNameFindsNothing()
        {
            Page("ffx2", "Gunner", "Songstress");
            Page("ffx", "Blitzball", "Al Bhed");
            Offers("FINAL FANTASY X-2", ("ffx2", "FINAL FANTASY X-2 HD Remaster"));
            Offers("final fantasy x hd remaster", ("ffx", "FINAL FANTASY X HD Remaster"));
            var targets = Game("FINAL FANTASY X", "Blitzball", "Al Bhed").Concat(Game("FINAL FANTASY X-2", "Gunner", "Songstress")).ToList();

            var choice = await Search().ChooseAsync(new[] { "FINAL FANTASY X/X-2 HD Remaster" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "ffx", "ffx2" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task NameVariant_IsSearched_WhenTheFullNameFindsNothing()
        {
            Page("rcom", "Card Master", "Floor Clear");
            Offers("Kingdom Hearts Re Chain of Memories", ("rcom", "KINGDOM HEARTS Re:Chain of Memories"));
            var targets = Game("Re:Chain", "Card Master", "Floor Clear");

            var choice = await Search().ChooseAsync(new[] { "Kingdom Hearts Re:Chain of Memories" }, targets, null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "rcom" }, choice.Ids.ToList());
            Assert.AreEqual("Kingdom Hearts Re:Chain of Memories", _searched[0]);
            CollectionAssert.Contains(_searched, "Kingdom Hearts Re Chain of Memories");
        }

        [TestMethod]
        public async Task PageMatchingTooFew_IsNotChosen()
        {
            Page("other", "Saren", "A", "B", "C", "D", "E");
            Offers("Game", ("other", "Game"));
            var targets = Game("Game", "Saren", "W", "X", "Y", "Z", "Q");

            Assert.IsNull(await Search().ChooseAsync(new[] { "Game" }, targets, null, CancellationToken.None));
        }

        [DataTestMethod]
        [DataRow("KINGDOM HEARTS Dream Drop Distance HD", "KINGDOM HEARTS Dream Drop Distance")]
        [DataRow("Mass Effect 1", "Mass Effect")]
        [DataRow("Kingdom Hearts Re:Chain of Memories", "Kingdom Hearts Re Chain of Memories")]
        [DataRow("Spyro (Reignited)", "Spyro")]
        [DataRow("Crash Bandicoot 2: Cortex Strikes Back", "Crash Bandicoot 2 Cortex")]
        [DataRow("Legendary Edition: Mass Effect 2", "Mass Effect 2")]
        [DataRow("Batman: Return to Arkham - Arkham City", "Arkham City")]
        public void Variants_IncludeBroaderNames(string name, string variant)
        {
            CollectionAssert.Contains(MetadataPageSearch.Variants(new[] { name }).ToList(), variant);
        }

        [TestMethod]
        public void Variants_DropWordsDownToOne_AndNeverRepeatTheName()
        {
            CollectionAssert.AreEqual(new[] { "Spyro the", "Spyro" }, MetadataPageSearch.Variants(new[] { "Spyro the Dragon" }).ToList());
            Assert.AreEqual(0, MetadataPageSearch.Variants(new[] { "Hades" }).Count);
        }

        [TestMethod]
        public async Task BroaderNames_StopAtTheFirstThatFindsAPage()
        {
            // Spyro the Dragon is only part of a collection on this source.
            Page("trilogy", "Gnasty's Demise", "Boom!");
            Offers("Spyro", ("trilogy", "Spyro Reignited Trilogy"));

            var choice = await Search().ChooseAsync(new[] { "Spyro the Dragon" }, Game("Spyro the Dragon", "Gnasty's Demise", "Boom!"), null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "trilogy" }, choice.Ids.ToList());
            CollectionAssert.AreEqual(new[] { "Spyro the Dragon", "Spyro the", "Spyro" }, _searched);
        }

        [TestMethod]
        public async Task Collection_GamesFoundOnOnePage_LinkItOnce()
        {
            Page("trilogy", "Saren", "Spectre", "Suicide Mission", "Collector");
            Offers("Mass Effect 1", ("trilogy", "Trilogy"));
            Offers("Mass Effect 2", ("trilogy", "Trilogy"));
            var targets = Game("Mass Effect 1", "Saren", "Spectre").Concat(Game("Mass Effect 2", "Suicide Mission", "Collector")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Nothing" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "trilogy" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task Collection_GameWithoutAPage_IsLeftOut()
        {
            Page("me1", "Saren", "Spectre");
            Offers("Mass Effect 1", ("me1", "Mass Effect"));
            var targets = Game("Mass Effect 1", "Saren", "Spectre").Concat(Game("Mass Effect 2", "Suicide Mission", "Collector")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Nothing" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "me1" }, choice.Ids.ToList());
            Assert.AreEqual(2, choice.Matched);
        }

        [TestMethod]
        public async Task OnePageMatchingEverything_SkipsTheSearchGameByGame()
        {
            Page("whole", "Saren", "Spectre", "Suicide Mission", "Collector");
            Offers("Trilogy", ("whole", "Trilogy"));
            var targets = Game("Mass Effect 1", "Saren", "Spectre").Concat(Game("Mass Effect 2", "Suicide Mission", "Collector")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Trilogy" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "whole" }, choice.Ids.ToList());
            CollectionAssert.AreEqual(new[] { "Trilogy" }, _searched);
        }

        [TestMethod]
        public async Task PageOfferedTwice_IsJudgedOnce()
        {
            var loads = 0;
            Page("game", "Saren", "Spectre");
            Offers("Game", ("other", "Other"), ("game", "Game"));
            Offers("Game Alt", ("game", "Game"));
            var search = new MetadataPageSearch(
                (name, ct) => Task.FromResult<IReadOnlyList<MetadataPageCandidate>>(_searches.TryGetValue(name, out var found) ? found : new List<MetadataPageCandidate>()),
                (id, ct) =>
                {
                    loads++;
                    return Task.FromResult<IList<AchievementDetail>>(_pages.TryGetValue(id, out var rows) ? rows : null);
                });

            await search.ChooseAsync(new[] { "Game", "Game Alt" }, Game("Game", "Saren", "Spectre"), null, CancellationToken.None);

            Assert.AreEqual(2, loads);
        }

        [TestMethod]
        public async Task CategoryTheChosenPageMisses_GetsItsOwnPage()
        {
            // Iceborne is a DLC category here and a separate trophy list on PlayStation.
            var baseTitles = new[] { "A", "B", "C", "D", "E", "F" };
            var dlcTitles = new[] { "G", "H", "I", "J", "K", "L" };
            _pages["mhw"] = baseTitles.Select((title, i) => Row("w" + i, title, null)).ToList();
            _pages["iceborne"] = dlcTitles.Select((title, i) => Row("i" + i, title, null)).ToList();
            Offers("Monster Hunter: World", ("mhw", "Monster Hunter: World"));
            Offers("Monster Hunter World: Iceborne", ("iceborne", "Monster Hunter World: Iceborne"));
            var targets = baseTitles.Select((title, i) => Row("b" + i, title, "Monster Hunter: World"))
                .Concat(dlcTitles.Select((title, i) => Row("d" + i, title, "Monster Hunter World: Iceborne", CategoryKindHelper.Dlc)))
                .ToList();

            var choice = await Search().ChooseAsync(new[] { "Monster Hunter: World" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "mhw", "iceborne" }, choice.Ids.ToList());
            Assert.AreEqual(12, choice.Matched);
        }

        [TestMethod]
        public async Task CategoryTheChosenPageCovers_IsNotSearched()
        {
            // The Binding of Isaac on consoles: one list with a few awards of each DLC.
            var titles = new[] { "A", "B", "C", "D", "E", "F" };
            var dlcTitles = new[] { "G", "H", "I", "J", "K", "L" };
            _pages["isaac"] = titles.Concat(new[] { "G", "H" }).Select((title, i) => Described(Row("p" + i, title, null))).ToList();
            Offers("The Binding of Isaac: Rebirth", ("isaac", "The Binding of Isaac: Rebirth"));
            var targets = titles.Select((title, i) => Described(Row("b" + i, title, "Rebirth")))
                .Concat(dlcTitles.Select((title, i) => Described(Row("d" + i, title, "Afterbirth", CategoryKindHelper.Dlc))))
                .ToList();

            var choice = await Search().ChooseAsync(new[] { "The Binding of Isaac: Rebirth" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "isaac" }, choice.Ids.ToList());
            CollectionAssert.DoesNotContain(_searched, "Afterbirth");
        }

        [TestMethod]
        public async Task CategoryWithATitleSharedByChance_IsStillSearched()
        {
            // "G" on the base list is another achievement than the DLC's "G": the descriptions differ.
            var titles = new[] { "A", "B", "C", "D", "E", "F" };
            var dlcTitles = new[] { "G", "H", "I", "J", "K", "L" };
            _pages["game"] = titles.Select((title, i) => Described(Row("p" + i, title, null)))
                .Concat(new[] { Described(Row("pg", "G", null), "Finish the base game on hard") })
                .ToList();
            _pages["dlc"] = dlcTitles.Select((title, i) => Described(Row("q" + i, title, null))).ToList();
            Offers("Game", ("game", "Game"));
            Offers("Expansion", ("dlc", "Expansion"));
            var targets = titles.Select((title, i) => Described(Row("b" + i, title, "Game")))
                .Concat(dlcTitles.Select((title, i) => Described(Row("d" + i, title, "Expansion", CategoryKindHelper.Dlc))))
                .ToList();

            var choice = await Search().ChooseAsync(new[] { "Game" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "game", "dlc" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task CategoryWithNoPageAndNoMatch_IsUncovered_AndLeavesDoubtAfterAnUnreadPage()
        {
            // Crash 1's page did not load: its category has nothing on the pages found.
            var titles = new[] { "A", "B", "C", "D", "E", "F" };
            var lostTitles = new[] { "G", "H", "I", "J", "K", "L" };
            _pages["game"] = titles.Select((title, i) => Row("p" + i, title, null)).ToList();
            Offers("Game", ("game", "Game"));
            var targets = titles.Select((title, i) => Row("b" + i, title, "Game"))
                .Concat(lostTitles.Select((title, i) => Row("d" + i, title, "Lost Game")))
                .ToList();

            var choice = await Search().ChooseAsync(new[] { "Game" }, targets, Array.Empty<MetadataSubGame>(), CancellationToken.None);
            var health = new MetadataLookupHealth();
            health.MarkUnreadable();

            CollectionAssert.AreEqual(new[] { "Lost Game" }, choice.Uncovered.ToList());
            Assert.IsTrue(health.LeavesDoubt(choice));
        }

        [TestMethod]
        public async Task FewAchievementsMissing_LeaveNoDoubtAfterAnUnreadPage()
        {
            // Tomb Raider on PlayStation: one achievement has no trophy; nothing is uncovered.
            var titles = new[] { "A", "B", "C", "D", "E", "F" };
            _pages["game"] = titles.Take(5).Select((title, i) => Row("p" + i, title, null)).ToList();
            Offers("Game", ("game", "Game"));
            var targets = titles.Select((title, i) => Row("b" + i, title, "Game")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Game" }, targets, Array.Empty<MetadataSubGame>(), CancellationToken.None);
            var health = new MetadataLookupHealth();
            health.MarkUnreadable();

            Assert.AreEqual(5, choice.Matched);
            Assert.AreEqual(0, choice.Uncovered.Count);
            Assert.IsFalse(health.LeavesDoubt(choice));
            Assert.IsTrue(health.LeavesDoubt(null), "no choice at all is still in doubt");
        }

        /// <summary>The row with a description made from its title, or the one given.</summary>
        private static AchievementDetail Described(AchievementDetail row, string description = null)
        {
            row.Description = description ?? "Earn the award named " + row.DisplayName;
            return row;
        }

        [TestMethod]
        public async Task SmallCategoryTheChosenPageMisses_IsNotSearched()
        {
            var titles = new[] { "A", "B", "C", "D", "E", "F" };
            _pages["game"] = titles.Select((title, i) => Row("g" + i, title, null)).ToList();
            Offers("Game", ("game", "Game"));
            var targets = titles.Select((title, i) => Row("b" + i, title, "Game"))
                .Concat(new[] { Row("d", "Bonus Skin", "Skin Pack", CategoryKindHelper.Dlc) })
                .ToList();

            var choice = await Search().ChooseAsync(new[] { "Game" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "game" }, choice.Ids.ToList());
            CollectionAssert.DoesNotContain(_searched, "Skin Pack");
        }

        [TestMethod]
        public async Task CategoryIsSearchedUnderNamesAnotherSourceFound()
        {
            Page("spyro1", "Gnasty", "Toasty");
            Page("spyro3", "Sorceress", "Bentley");
            Offers("Spyro the Dragon", ("spyro1", "Spyro the Dragon"));
            Offers("Spyro 3: Year of the Dragon", ("spyro3", "Spyro 3: Year of the Dragon"));
            var targets = Game("Spyro the Dragon", "Gnasty", "Toasty").Concat(Game("Spyro: Year of the Dragon", "Sorceress", "Bentley")).ToList();
            var known = new Dictionary<string, IReadOnlyList<string>> { ["Spyro: Year of the Dragon"] = new[] { "Spyro 3: Year of the Dragon" } };

            var choice = await Search().ChooseAsync(new[] { "Spyro Reignited Trilogy" }, targets, MetadataSubGame.From(targets), CancellationToken.None, known);

            CollectionAssert.AreEqual(new[] { "spyro1", "spyro3" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task Choice_KeepsTheTitleEachGameWasFoundUnder()
        {
            Page("me1", "Saren", "Spectre");
            Page("me2", "Suicide Mission", "Collector");
            Offers("Mass Effect 1", ("me1", "Legendary Edition: Mass Effect"));
            Offers("Mass Effect 2", ("me2", "Legendary Edition: Mass Effect 2"));
            var targets = Game("Mass Effect 1", "Saren", "Spectre").Concat(Game("Mass Effect 2", "Suicide Mission", "Collector")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Nothing" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            Assert.AreEqual(
                "Mass Effect 1\tLegendary Edition: Mass Effect\nMass Effect 2\tLegendary Edition: Mass Effect 2",
                choice.Label);
        }

        [TestMethod]
        public void FoundNames_RoundTrip_AndReadAPlainLabelAsTheWholeGame()
        {
            var names = new List<(string, string)> { (null, "Whole Game"), ("Game 2", "Game II Remastered") };

            var parsed = MetadataFoundNames.Parse(MetadataFoundNames.Format(names));

            CollectionAssert.AreEqual(names, parsed.ToList());
            CollectionAssert.AreEqual(new List<(string, string)> { (null, "Test game") }, MetadataFoundNames.Parse("Test game").ToList());
            Assert.AreEqual(0, MetadataFoundNames.Parse(null).Count);
            Assert.IsNull(MetadataFoundNames.Format(null));
        }

        [TestMethod]
        public async Task PageMatchingEverything_StopsTheSearch()
        {
            Page("full", "Saren", "Spectre");
            Page("later", "Saren", "Spectre");
            Offers("Game", ("full", "Game"), ("later", "Game Again"));
            Offers("Game Alt", ("other", "Other"));
            var loads = new List<string>();
            var search = new MetadataPageSearch(
                (name, ct) => Task.FromResult<IReadOnlyList<MetadataPageCandidate>>(_searches.TryGetValue(name, out var found) ? found : new List<MetadataPageCandidate>()),
                (id, ct) =>
                {
                    loads.Add(id);
                    return Task.FromResult<IList<AchievementDetail>>(_pages.TryGetValue(id, out var rows) ? rows : null);
                });

            var choice = await search.ChooseAsync(new[] { "Game", "Game Alt" }, Game("Game", "Saren", "Spectre"), null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "full" }, choice.Ids.ToList());
            CollectionAssert.AreEqual(new[] { "full" }, loads);
        }

        [TestMethod]
        public async Task PageAnotherSourcePointedTo_IsTakenWithoutASearch()
        {
            Page("xbox-page", "Saren", "Spectre");
            var seeds = new[] { new MetadataPageCandidate("xbox-page", null) };

            var choice = await Search().ChooseAsync(new[] { "Mass Effect" }, Game("Game", "Saren", "Spectre"), null, CancellationToken.None, seeds: seeds);

            CollectionAssert.AreEqual(new[] { "xbox-page" }, choice.Ids.ToList());
            Assert.AreEqual(0, _searched.Count);
        }

        [TestMethod]
        public async Task PageAnotherSourcePointedTo_ThatMatchesTooLittle_LeavesTheSearch()
        {
            Page("wrong", "Other", "Things");
            Page("right", "Saren", "Spectre");
            Offers("Mass Effect", ("right", "Mass Effect"));

            var choice = await Search().ChooseAsync(
                new[] { "Mass Effect" }, Game("Game", "Saren", "Spectre"), null, CancellationToken.None,
                seeds: new[] { new MetadataPageCandidate("wrong", null) });

            CollectionAssert.AreEqual(new[] { "right" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task Choice_TracesHowItsPageWasFound()
        {
            Page("me", "Saren", "Spectre");
            _searches["Mass Effect"] = new List<MetadataPageCandidate> { new MetadataPageCandidate("me", "Mass Effect", "search #1") };

            var choice = await Search().ChooseAsync(new[] { "Mass Effect" }, Game("Game", "Saren", "Spectre"), null, CancellationToken.None);

            StringAssert.Contains(choice.Trace, "whole game: search #1 via name 'Mass Effect'");
            StringAssert.Contains(choice.Trace, "1 pages judged");
        }

        [TestMethod]
        public async Task Collection_KeepsTheWholePage_WhenItsGamesPagesOverlapIt()
        {
            // The collection page lists every game; one game's search also offers it, so the
            // games' own counts would add the same rows twice.
            Page("whole", "A1", "A2", "B1", "B2");
            Page("game-a", "A1", "A2");
            Offers("Collection", ("whole", "Collection"));
            Offers("Game A", ("game-a", "Game A"));
            Offers("Game B", ("whole", "Collection"));
            var targets = Game("Game A", "A1", "A2", "Extra").Concat(Game("Game B", "B1", "B2")).ToList();

            var choice = await Search().ChooseAsync(new[] { "Collection" }, targets, MetadataSubGame.From(targets), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "whole" }, choice.Ids.ToList());
        }

        [TestMethod]
        public async Task NoPageAndNoSubGames_ChoosesNothing()
        {
            Assert.IsNull(await Search().ChooseAsync(new[] { "Game" }, Game("Game", "Saren"), null, CancellationToken.None));
        }

        [TestMethod]
        public void SubGames_AreBaseCategories_OnlyWhenThereAreTwo()
        {
            var one = Game("Base Game", "A").Concat(new[] { Row("d", "B", "Expansion", CategoryKindHelper.Dlc) }).ToList();
            var two = Game("Game 1", "A").Concat(Game("Game 2", "B")).Concat(new[] { Row("d", "C", "Expansion", CategoryKindHelper.Dlc) }).ToList();

            Assert.AreEqual(0, MetadataSubGame.From(one).Count);
            CollectionAssert.AreEqual(new[] { "Game 1", "Game 2" }, MetadataSubGame.From(two).Select(game => game.Name).ToList());
        }

        [TestMethod]
        public void Affix_IsWhatTheTitleAddsAroundTheName()
        {
            Assert.AreEqual(("", "hd remaster"), MetadataPageSearch.Affix("FINAL FANTASY X-2", "FINAL FANTASY X-2 HD Remaster"));
            Assert.AreEqual(("legendary edition", ""), MetadataPageSearch.Affix("Mass Effect 2", "Legendary Edition: Mass Effect 2"));
            Assert.IsNull(MetadataPageSearch.Affix("Mass Effect 2", "Mass Effect 3"));
        }

        [DataTestMethod]
        [DataRow("C1 | ")]
        [DataRow("TR:UB | ")]
        [DataRow("Shenmue: ")]
        public void RepeatedTitlePrefix_IsIgnoredWhenMatching(string prefix)
        {
            var titles = new[] { "Papu Popped", "Ripper Ruined", "Spin Doctor" };
            var targets = titles.Select((title, i) => Row("t" + i, prefix + title, null)).ToList();
            var sources = titles.Select((title, i) => Row("s" + i, title, null)).ToList();

            Assert.AreEqual(3, CrossSourceAchievementMatcher.Match(targets, sources).Count);
        }

        [TestMethod]
        public void RepeatedOrdinaryFirstWord_StillMatchesAsWritten()
        {
            var titles = new[] { "The Hunt", "The Chase", "The End" };
            var targets = titles.Select((title, i) => Row("t" + i, title, null)).ToList();
            var sources = titles.Select((title, i) => Row("s" + i, title, null)).ToList();

            Assert.AreEqual(3, CrossSourceAchievementMatcher.Match(targets, sources).Count);
        }

        [TestMethod]
        public void TitleWithAColon_StillMatchesAsWritten()
        {
            var targets = new List<AchievementDetail> { Row("t", "Mission: Accomplished", null) };
            var sources = new List<AchievementDetail> { Row("s", "Mission: Accomplished", null) };

            Assert.AreEqual(1, CrossSourceAchievementMatcher.Match(targets, sources).Count);
        }

        [TestMethod]
        public void TitleSharedByTwoGames_PairsWithinEachGamesList()
        {
            var targets = Game("Kingdom Hearts", "Undefeated", "Kingdom Hearts Master")
                .Concat(Game("Kingdom Hearts II", "Undefeated", "Kingdom Hearts II Master"))
                .ToList();
            var first = new List<AchievementDetail> { Row("a1", "Undefeated", null), Row("a2", "Kingdom Hearts Master", null) };
            var second = new List<AchievementDetail> { Row("b1", "Undefeated", null), Row("b2", "Kingdom Hearts II Master", null) };
            var lists = new[] { new MetadataSourceList("kh1", "Kingdom Hearts", first), new MetadataSourceList("kh2", "Kingdom Hearts II", second) };
            var sources = first.Concat(second).ToList();

            var pooled = CrossSourceAchievementMatcher.Match(targets, sources);
            var byList = MetadataOverlayRun.MatchByList(targets, sources, lists, matchApiNames: false);

            Assert.AreEqual(2, pooled.Count);
            Assert.AreEqual(4, byList.Count);
            Assert.AreEqual("b1", byList.Single(match => match.Target == targets[2]).Source.ApiName);
        }

        // The game's achievements in English, by the targets' ApiNames, and how often they were asked for.
        private readonly List<AchievementDetail> _english = new List<AchievementDetail>();
        private int _englishAsked;

        private Task<IList<AchievementDetail>> EnglishTitles(CancellationToken ct)
        {
            _englishAsked++;
            return Task.FromResult<IList<AchievementDetail>>(_english);
        }

        [TestMethod]
        public async Task EnglishTitles_FindThePage_WhenNoTitleMatchesInTheGamesLanguage()
        {
            Page("me", "Saren", "Spectre");
            Offers("Mass Effect", ("me", "Mass Effect"));
            var targets = new List<AchievementDetail> { Row("g1", "Saren besiegt", null), Row("g2", "Spectre-Agent", null) };
            _english.Add(Row("g1", "Saren", null));
            _english.Add(Row("g2", "Spectre", null));

            var choice = await Search().ChooseAsync(new[] { "Mass Effect" }, targets, null, CancellationToken.None, englishTitles: EnglishTitles);

            CollectionAssert.AreEqual(new[] { "me" }, choice.Ids.ToList());
            Assert.AreEqual(2, choice.Matched);
            StringAssert.Contains(choice.Trace, "in English");
        }

        [TestMethod]
        public async Task EnglishTitles_AreNotAskedFor_WhenAPageMatchesAsGiven()
        {
            Page("me", "Saren", "Spectre");
            Offers("Mass Effect", ("me", "Mass Effect"));
            var targets = new List<AchievementDetail> { Row("g1", "Saren", null), Row("g2", "Spectre", null) };

            var choice = await Search().ChooseAsync(new[] { "Mass Effect" }, targets, null, CancellationToken.None, englishTitles: EnglishTitles);

            CollectionAssert.AreEqual(new[] { "me" }, choice.Ids.ToList());
            Assert.AreEqual(0, _englishAsked);
        }

        [TestMethod]
        public async Task EnglishRows_OfAPageInAnotherLanguage_AreComparedWithTheGamesEnglish()
        {
            // The source translates the titles differently from the game; both agree in English.
            Page("me", "Saren besiegt", "Agent der Spectres");
            Offers("Mass Effect", ("me", "Mass Effect"));
            var targets = new List<AchievementDetail> { Row("g1", "Saren geschlagen", null), Row("g2", "Spectre-Agent", null) };
            _english.Add(Row("g1", "Saren", null));
            _english.Add(Row("g2", "Spectre", null));
            var englishPage = new List<AchievementDetail> { Row("me0", "Saren", null), Row("me1", "Spectre", null) };
            var search = new MetadataPageSearch(
                (name, ct) => Task.FromResult<IReadOnlyList<MetadataPageCandidate>>(_searches.TryGetValue(name, out var found) ? found : new List<MetadataPageCandidate>()),
                (id, ct) => Task.FromResult<IList<AchievementDetail>>(_pages.TryGetValue(id, out var rows) ? rows : null),
                englishRows: (id, ct) => Task.FromResult<IList<AchievementDetail>>(id == "me" ? englishPage : null));

            var choice = await search.ChooseAsync(new[] { "Mass Effect" }, targets, null, CancellationToken.None, englishTitles: EnglishTitles);

            CollectionAssert.AreEqual(new[] { "me" }, choice.Ids.ToList());
            Assert.AreEqual(2, choice.Matched);
        }

        [TestMethod]
        public void EnglishPairing_JoinsByApiName_AndSkipsRowsWithoutEnglish()
        {
            var targets = new List<AchievementDetail> { Row("g1", "Drachentöter", null), Row("g2", "Sammler", null) };
            var rows = new List<AchievementDetail> { Row("s1", "Pourfendeur", null), Row("s2", "Collectionneur", null) };
            var englishTargets = new List<AchievementDetail> { Row("g1", "Dragon Slayer", null), Row("g2", "Collector", null) };
            var englishRows = new List<AchievementDetail> { Row("s1", "Dragon Slayer", null) };

            var matches = EnglishTitlePairing.Match(targets, englishTargets, rows, englishRows);

            Assert.AreEqual(1, matches.Count);
            Assert.AreSame(targets[0], matches[0].Target);
            Assert.AreSame(rows[0], matches[0].Source);
        }

        [TestMethod]
        public void English_IsTheDefaultLanguage()
        {
            Assert.IsTrue(EnglishTitlePairing.IsEnglish(null));
            Assert.IsTrue(EnglishTitlePairing.IsEnglish(" English "));
            Assert.IsFalse(EnglishTitlePairing.IsEnglish("german"));
        }
    }
}
