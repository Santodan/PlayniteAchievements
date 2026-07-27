using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Search;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class SearchTextIndexTests
    {
        [TestMethod]
        public void SearchQuery_TreatsWhitespaceAsNoSearch()
        {
            var query = SearchQuery.From("   ");

            Assert.IsFalse(query.HasValue);
            Assert.IsTrue(query.Matches(null));
            Assert.IsTrue(query.Matches("anything"));
        }

        [TestMethod]
        public void SearchQuery_MatchesCaseInsensitiveSubstring()
        {
            var query = SearchQuery.From("BOSS");

            Assert.IsTrue(query.HasValue);
            Assert.IsTrue(query.Matches("Defeat the ancient boss"));
            Assert.IsFalse(query.Matches("Collect ten coins"));
        }

        [TestMethod]
        public void SearchTextBuilder_IncludesSurfaceSpecificFields()
        {
            var noteText = SearchTextBuilder.ForManageNote(
                "Display Name",
                "Description",
                "api.name",
                "Pinned note",
                "DLC",
                "Challenge");

            Assert.IsTrue(SearchQuery.From("display").Matches(noteText));
            Assert.IsTrue(SearchQuery.From("api.name").Matches(noteText));
            Assert.IsTrue(SearchQuery.From("pinned").Matches(noteText));
            Assert.IsTrue(SearchQuery.From("challenge").Matches(noteText));
        }

        [TestMethod]
        public void SearchTextIndex_UsesCachedTextUntilInvalidated()
        {
            var row = new SearchRow { Name = "First Name", Description = "Original Description" };
            var index = new SearchTextIndex<SearchRow>(item =>
                SearchTextBuilder.ForManualEdit(item.Name, item.Description, item.ApiName));

            index.Rebuild(new[] { row });

            Assert.IsTrue(index.Matches(row, SearchQuery.From("original")));

            row.Description = "Updated Description";

            Assert.IsTrue(index.Matches(row, SearchQuery.From("original")));
            Assert.IsFalse(index.Matches(row, SearchQuery.From("updated")));

            index.Invalidate(row);

            Assert.IsFalse(index.Matches(row, SearchQuery.From("original")));
            Assert.IsTrue(index.Matches(row, SearchQuery.From("updated")));
        }

        private sealed class SearchRow
        {
            public string Name { get; set; }

            public string Description { get; set; }

            public string ApiName { get; set; }
        }
    }
}
