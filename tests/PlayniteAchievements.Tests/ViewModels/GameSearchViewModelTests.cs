using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class GameSearchViewModelTests
    {
        [TestMethod]
        public async Task Search_RanksResultsAndSelectsTheBest()
        {
            var source = new FakeSource(results: new[] { R("2", "Hades II"), R("1", "Hades") });
            var viewModel = new GameSearchViewModel(source, "Hades");

            await viewModel.SearchAsync();

            CollectionAssert.AreEqual(new[] { "1", "2" }, viewModel.Results.Select(item => item.Result.Id).ToArray());
            Assert.AreEqual("1", viewModel.SelectedItem.Result.Id);
        }

        [TestMethod]
        public async Task Search_SelectsTheCurrentLinkOverTheBestMatch()
        {
            var source = new FakeSource(results: new[] { R("1", "Hades"), R("2", "Hades II") });
            var viewModel = new GameSearchViewModel(source, "Hades", new[] { R("2", "Hades II") });

            await viewModel.SearchAsync();

            Assert.AreEqual("2", viewModel.SelectedItem.Result.Id);
            Assert.AreEqual(0, viewModel.Picked.Count);
        }

        [TestMethod]
        public async Task Search_ListsTheLinkedGameFirstOnOpenAndSelectsIt()
        {
            var source = new FakeSource(results: new[] { R("1", "Hades") });
            var viewModel = new GameSearchViewModel(source, "Hades", new[] { R("9", "Hades (2018 Edition)") });

            await viewModel.SearchAsync();

            CollectionAssert.AreEqual(new[] { "9", "1" }, viewModel.Results.Select(item => item.Result.Id).ToArray());
            Assert.AreSame(viewModel.Results[0], viewModel.SelectedItem);

            viewModel.SearchText = "Other";
            await viewModel.SearchAsync();

            Assert.IsFalse(viewModel.Results.Any(item => item.Result.Id == "9"));
        }

        [TestMethod]
        public async Task AddLink_EnablesOnlyForAReadableLinkAndAddsItFirst()
        {
            var source = new FakeSource(
                results: new[] { R("1", "Hades"), R("42", "Hades II") },
                parse: text => text.Contains("/game/") ? "42" : null,
                describe: id => R(id, "Hades II")) { Multiple = true };
            var viewModel = new GameSearchViewModel(source, "Hades");
            await viewModel.SearchAsync();

            viewModel.LinkText = "Hades";
            Assert.IsFalse(viewModel.AddLinkCommand.CanExecute(null));

            viewModel.LinkText = "https://example.org/game/42";
            Assert.IsTrue(viewModel.AddLinkCommand.CanExecute(null));
            await viewModel.AddLinkAsync();

            CollectionAssert.AreEqual(new[] { "42", "1" }, viewModel.Results.Select(item => item.Result.Id).ToArray());
            Assert.AreEqual("42", viewModel.SelectedItem.Result.Id);
            Assert.IsTrue(viewModel.Results[0].IsChecked);
            Assert.AreEqual(string.Empty, viewModel.LinkText);
        }

        [TestMethod]
        public async Task Search_PutsATypedIdFirstAndDropsItsDuplicate()
        {
            var source = new FakeSource(
                results: new[] { R("hades", "Hades"), R("hades-steam", "Hades (search copy)") },
                parse: text => text == "hades-steam" ? "hades-steam" : null,
                describe: id => R(id, "Hades (Steam)"));
            var viewModel = new GameSearchViewModel(source, "hades-steam");

            await viewModel.SearchAsync();

            Assert.AreEqual(2, viewModel.Results.Count);
            Assert.AreEqual("hades-steam", viewModel.Results[0].Result.Id);
            Assert.AreEqual("Hades (Steam)", viewModel.Results[0].Name);
            Assert.IsTrue(viewModel.Results[0].Result.IsDirect);
            Assert.AreSame(viewModel.Results[0], viewModel.SelectedItem);
        }

        [TestMethod]
        public async Task Search_AnAddressIsNotSearchedAsText()
        {
            var source = new FakeSource(
                results: new[] { R("x", "Noise") },
                parse: text => text.Contains("/game/") ? "42" : null,
                describe: id => null);
            var viewModel = new GameSearchViewModel(source, "https://example.org/game/42");

            await viewModel.SearchAsync();

            Assert.AreEqual(0, source.SearchCount);
            Assert.AreEqual(1, viewModel.Results.Count);
            Assert.AreEqual("42", viewModel.Results[0].Result.Id);
            Assert.AreEqual("42", viewModel.Results[0].Name);
        }

        [TestMethod]
        public async Task Search_ANewSearchCancelsTheOneInFlight()
        {
            var slow = new TaskCompletionSource<IReadOnlyList<GameSearchResult>>();
            var source = new FakeSource(results: new[] { R("b", "Second") }) { Pending = slow };
            var viewModel = new GameSearchViewModel(source, "first");

            var first = viewModel.SearchAsync();
            source.Pending = null;
            viewModel.SearchText = "second";
            await viewModel.SearchAsync();
            slow.SetResult(new[] { R("a", "First") });
            await first;

            Assert.IsTrue(source.FirstToken.IsCancellationRequested);
            CollectionAssert.AreEqual(new[] { "b" }, viewModel.Results.Select(item => item.Result.Id).ToArray());
            Assert.IsFalse(viewModel.IsSearching);
        }

        [TestMethod]
        public async Task Multiple_PickedGamesKeepTheirOrderAcrossSearches()
        {
            var source = new FakeSource(results: new[] { R("1", "Mass Effect"), R("2", "Mass Effect 2"), R("3", "Mass Effect 3") }) { Multiple = true };
            var viewModel = new GameSearchViewModel(source, "Mass Effect");
            await viewModel.SearchAsync();

            viewModel.Results.Single(item => item.Result.Id == "3").IsChecked = true;
            viewModel.Results.Single(item => item.Result.Id == "1").IsChecked = true;
            await viewModel.SearchAsync();

            CollectionAssert.AreEqual(new[] { "3", "1" }, viewModel.GetSelection().Select(result => result.Id).ToArray());
            Assert.IsTrue(viewModel.Results.Single(item => item.Result.Id == "3").IsChecked);
            Assert.IsFalse(viewModel.Results.Single(item => item.Result.Id == "2").IsChecked);
        }

        [TestMethod]
        public async Task Multiple_RemovingAChipUnchecksItsRow()
        {
            var source = new FakeSource(results: new[] { R("1", "Mass Effect"), R("2", "Mass Effect 2") }) { Multiple = true };
            var viewModel = new GameSearchViewModel(source, "Mass Effect", new[] { R("1", "Mass Effect"), R("2", "Mass Effect 2") });
            await viewModel.SearchAsync();

            viewModel.RemovePickCommand.Execute(viewModel.Picked[0]);

            CollectionAssert.AreEqual(new[] { "2" }, viewModel.Picked.Select(result => result.Id).ToArray());
            Assert.IsFalse(viewModel.Results.Single(item => item.Result.Id == "1").IsChecked);
        }

        [TestMethod]
        public async Task Multiple_NothingPickedUsesTheHighlightedRow()
        {
            var source = new FakeSource(results: new[] { R("1", "Mass Effect") }) { Multiple = true };
            var viewModel = new GameSearchViewModel(source, "Mass Effect", new[] { R("1", "Mass Effect") });
            await viewModel.SearchAsync();

            Assert.AreEqual(0, viewModel.Picked.Count);
            CollectionAssert.AreEqual(new[] { "1" }, viewModel.GetSelection().Select(result => result.Id).ToArray());
        }

        private static GameSearchResult R(string id, string name) => new GameSearchResult(id, name);

        private sealed class FakeSource : IGameSearchSource
        {
            private readonly IReadOnlyList<GameSearchResult> _results;
            private readonly Func<string, string> _parse;
            private readonly Func<string, GameSearchResult> _describe;

            public FakeSource(
                IReadOnlyList<GameSearchResult> results,
                Func<string, string> parse = null,
                Func<string, GameSearchResult> describe = null)
            {
                _results = results;
                _parse = parse ?? (_ => null);
                _describe = describe ?? (id => new GameSearchResult(id, id));
            }

            public string DisplayName => "Fake";

            public bool AllowsMultiple => Multiple;

            public bool Multiple { get; set; }

            public TaskCompletionSource<IReadOnlyList<GameSearchResult>> Pending { get; set; }

            public int SearchCount { get; private set; }

            public CancellationToken FirstToken { get; private set; }

            public Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct)
            {
                if (SearchCount++ == 0)
                {
                    FirstToken = ct;
                }

                return Pending?.Task ?? Task.FromResult(_results);
            }

            public string TryParseId(string text) => _parse(text);
            public Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct) => Task.FromResult(_describe(id));
        }
    }
}
