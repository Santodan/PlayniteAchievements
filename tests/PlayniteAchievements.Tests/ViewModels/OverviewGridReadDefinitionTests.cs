using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The Overview reads its Achievements grid and its Games grid's achievement filter through
    /// queries instead of holding every unlock. OverviewViewModel is not linked into this project,
    /// so the wiring is pinned against the source; OverviewGridQueryTests runs the same reads on a
    /// seeded database.
    /// </summary>
    [TestClass]
    public class OverviewGridReadDefinitionTests
    {
        [TestMethod]
        public void TheFilterRead_IsTheUnlocksUnderTheSummaryRules_WithEveryAchievementFilter()
        {
            var filters = Between(Read("OverviewViewModel.cs"), "private AchievementQuery AchievementFilterQuery(", "private void UpdateRecentQuery()");

            StringAssert.Contains(filters, "_tierFilters.ApplyTo((source ?? AchievementQuery.CurrentUser.WithSummaryRules()).WhereUnlocked(true))");
            StringAssert.Contains(filters, "WhereUnlockedBetween(_unlockRangeFrom, _unlockRangeTo)");
            StringAssert.Contains(filters, "WhereClassification(_selectedTagFilters.ToList())");
        }

        [TestMethod]
        public void TheGrid_ReadsTheListedGames_Search_Sort_AndCap_AsPagedRows()
        {
            var source = Read("OverviewViewModel.cs");
            var update = Between(source, "private void UpdateRecentQuery()", "// The column sort, else the configured default");

            StringAssert.Contains(update, "AchievementFilterQuery().ForGames(_filteredGameSummaries");
            StringAssert.Contains(update, "if (ListsEveryGame())");
            StringAssert.Contains(update, "AchievementFilterQuery(_everyGameQuery ??= _achievementDataService.SummaryQuery())", "with no game filter the grid reads at open");
            StringAssert.Contains(update, "query.WhereSearch(RightSearchText)");
            StringAssert.Contains(update, "query = ApplyRecentSort(query);");
            StringAssert.Contains(update, "NormalizeGridMaxRows(_settings?.Persisted?.OverviewRecentAchievementsGridMaxRows)");
            StringAssert.Contains(update, "new PagedRows<AchievementDisplayItem>(");
            StringAssert.Contains(update, "new AchievementDisplayItem { IsPlaceholder = true }");
            StringAssert.Contains(update, "cap ?? 0");
            Assert.IsFalse(update.Contains("Feed"), "the grid reads again with each summary reload, not on its own");
            StringAssert.Contains(update, "else if (dataChanged)");
            StringAssert.Contains(update, "_recent.Refresh();");
            StringAssert.Contains(update, "update: (kept, fresh) => kept.RefreshFrom(fresh)");

            var sort = Between(source, "private AchievementQuery ApplyRecentSort(", "private IReadOnlyList<AchievementDisplayItem> LoadRecentRows(");
            StringAssert.Contains(sort, "AchievementQuery.SortOfColumn(_recentSortPath)");
            StringAssert.Contains(sort, "AchievementSortSurface.OverviewRecentAchievements");
            StringAssert.Contains(sort, "query.OrderBy(AchievementSort.UnlockTime, descending: true)");

            var click = Between(source, "private void SortRecentAchievements(", "private void SortSelectedGameAchievements(");
            StringAssert.Contains(click, "if (AchievementQuery.SortOfColumn(sortMemberPath) == null)");
            StringAssert.Contains(click, "UpdateRecentQuery();");
        }

        [TestMethod]
        public void TheGridsRead_NeedsNoSnapshot_AndRunsOncePerOpen()
        {
            var source = Read("OverviewViewModel.cs");
            var load = Between(source, "private IReadOnlyList<AchievementDisplayItem> LoadRecentRows(", "private List<AchievementDisplayItem> LoadedRecentRows()");
            StringAssert.Contains(load, "_dataBuilder.LoadRows(_settings, query, keys)", "the read names an authored row's provider itself (AchievementReadServiceTests)");
            Assert.IsFalse(source.Contains("_gamesById"), "rows read before the snapshot need nothing from it");

            // The one row loader every grid and widget reads through takes no games.
            var rows = Between(
                File.ReadAllText(FindRepoFile("source", "Services", "Overview", "OverviewDataBuilder.cs")),
                "internal List<AchievementDisplayItem> LoadRows(",
                "internal List<AchievementDisplayItem> BuildRows(");
            Assert.IsFalse(rows.Contains("games.TryGetValue"), "no games lookup fills the provider");
            StringAssert.Contains(rows, "return BuildRows(settings, rows);");

            // The snapshot that lands at open does not read the grids again; a kept view shown
            // again after a change does.
            var activate = Between(source, "public void SetActive(bool isActive)", "public Task RefreshViewAsync()");
            StringAssert.Contains(activate, "RefreshViewAsync(opening: _latestSnapshot == null)");
            StringAssert.Contains(source, "ApplySnapshot(snapshot, gameEntries, readsCurrent: opening)");
            var apply = Between(source, "private void ApplySnapshot(", "private bool IsTransientRebuildSnapshot");
            StringAssert.Contains(apply, "if (!readsCurrent)");
        }

        [TestMethod]
        public void TheGamesGridFilter_ReadsTheGameKeys_AndAppliedSummariesReadEverythingAgain()
        {
            var linked = Read("OverviewViewModel.Linked.cs");
            var matching = Between(linked, "private HashSet<Guid> MatchingGames()", "public OverviewDataSnapshot GetLinkedSnapshot(");
            StringAssert.Contains(matching, "AchievementFilterQuery()");
            StringAssert.Contains(matching, "Keys(read, AchievementGroup.Game)");
            StringAssert.Contains(matching, "ApplyLeftFilters();");
            Assert.IsFalse(linked.Contains("_allRecentAchievements"), "no copy of every unlock is held");

            var apply = Between(Read("OverviewViewModel.cs"), "private void ApplySnapshot(", "private bool IsTransientRebuildSnapshot");
            StringAssert.Contains(apply, "_matchingGames?.SetQuery(_matchingGames.Query);");
            StringAssert.Contains(apply, "_recentDataChanged = true;");
            StringAssert.Contains(apply, "_tierFilters.UpdateGames(_allGameSummaries)");
        }

        private static string Read(string file) =>
            File.ReadAllText(FindRepoFile("source", "ViewModels", file));

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {string.Join(Path.DirectorySeparatorChar.ToString(), parts)}.");
            return null;
        }
    }
}
