using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// A one-game custom-data edit used to clear the entire hydrated summary memo, so the next
    /// read re-ran five unfiltered whole-library queries. The memo now carries a dirty set and
    /// the next read patches only those games.
    ///
    /// AchievementDataService is not linked into this project, so these assert against the
    /// source the way the other wiring definition tests here do. The patch arithmetic itself is
    /// covered behaviourally by OverviewSummaryPatcherTests.
    /// </summary>
    [TestClass]
    public class OverviewSummaryMemoDefinitionTests
    {
        [TestMethod]
        public void BothInvalidationEntryPoints_PassTheScopeTheyAlreadyCarry()
        {
            var source = ReadService();

            var customData = Between(
                source,
                "private void OnCustomDataChangedForOverview(",
                "private void OnCacheInvalidatedForOverview(");

            // The flag check must stay first: a reorder-only edit does no work at all, and must
            // not be made to do more by the scoping change.
            var earlyReturn = customData.IndexOf("!e.AffectsSummaryData", StringComparison.Ordinal);
            var invalidate = customData.IndexOf("InvalidateOverviewProjectionCaches(", StringComparison.Ordinal);
            Assert.IsTrue(earlyReturn >= 0, "The AffectsSummaryData early return must remain.");
            Assert.IsTrue(
                earlyReturn < invalidate,
                "The early return has to precede the invalidation, or a goals reorder starts " +
                "paying for a summary patch it cannot have moved.");

            var cacheInvalidated = Between(
                source,
                "private void OnCacheInvalidatedForOverview(",
                "private void SyncAchievementFiltersForGame(");

            StringAssert.Contains(
                cacheInvalidated,
                "e.ChangedGameIds",
                "The editor's scoped raise is the only route by which an edit that does not " +
                "affect summary data reaches the overview; honouring its scope is what stops " +
                "that raise costing a whole-library rebuild.");
            StringAssert.Contains(cacheInvalidated, "e?.IsFull == false");
        }

        [TestMethod]
        public void TheMirrorWrite_StillPrecedesTheMemoMutation()
        {
            var body = Between(
                ReadService(),
                "private void OnCustomDataChangedForOverview(",
                "private void OnCacheInvalidatedForOverview(");

            var mirror = body.IndexOf("SyncAchievementFiltersForGame(", StringComparison.Ordinal);
            var memo = body.IndexOf("InvalidateOverviewProjectionCaches(", StringComparison.Ordinal);

            Assert.IsTrue(mirror >= 0 && memo > mirror,
                "Ordering invariant: the filter mirror is written before the summary memo is " +
                "touched, so no reader can cache a summary built against stale mirror rows.");
        }

        [TestMethod]
        public void OnlyTheUnboundedEntry_IsPatched()
        {
            var body = Between(
                ReadService(),
                "private void InvalidateOverviewProjectionCaches(",
                "/// <summary>\r\n        /// Memoized overview summaries retained right now");

            StringAssert.Contains(
                body,
                "limit != 0",
                "Bounded entries trim rows library-wide and a trimmed row cannot be recovered " +
                "from one game's slice, so they are dropped rather than patched.");
            StringAssert.Contains(
                body,
                "_overviewProjectionGeneration++",
                "The generation must still be bumped on every invalidation, scoped or not, or a " +
                "summary loaded before the change could be memoized after it.");
        }

        [TestMethod]
        public void AnOverLargeChangeSet_FallsBackToTheWholesalePath()
        {
            var body = Between(
                ReadService(),
                "private void InvalidateOverviewProjectionCaches(",
                "/// <summary>\r\n        /// Memoized overview summaries retained right now");

            Assert.AreEqual(
                2,
                CountOccurrences(body, "MaxScopedGames"),
                "Two caps: the incoming change set, and the accumulated dirty set. Past either " +
                "the patch stops paying for itself against a rebuild.");
            StringAssert.Contains(body, "_overviewSummaryCacheByLimit.Clear()");
        }

        [TestMethod]
        public void ThePatch_InstallsOnlyWhenNoInvalidationLandedWhileItWasBuilt()
        {
            var body = Between(
                ReadService(),
                "private CachedSummaryData TryPatchOverviewSummary(",
                "internal CachedSummaryData GetCachedSummaryDataForTheme(");

            StringAssert.Contains(
                body,
                "generation == _overviewProjectionGeneration",
                "Same rule the full rebuild uses: a result built against since-invalidated state " +
                "may be returned to this caller but must not outlive the invalidation in the memo.");
            StringAssert.Contains(
                body,
                "normalizedLimit != 0",
                "Only the unbounded read is patchable.");
            StringAssert.Contains(
                body,
                "if (slice == null)",
                "A scoped read that failed must fall back to a rebuild, not be treated as a game " +
                "that now contributes nothing.");
        }

        [TestMethod]
        public void TheScopedHydration_RunsTheSameCodeAsTheWholeLibraryPath()
        {
            var source = ReadService();

            StringAssert.Contains(
                source,
                "ApplyOverviewSummaryHydration(slice, 0, new[] { gameId })",
                "Reusing the whole-library hydration, narrowed by scope, is the guarantee that a " +
                "patched summary equals a full rebuild. A second hydration path would drift.");

            StringAssert.Contains(
                source,
                "BuildOverviewCustomDataContext(scopeGameIds)",
                "The custom-data context must narrow too: LoadAll deep-clones every stored " +
                "record on every call.");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadService()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            var parts = new[] { "source", "Services", "Achievements", "AchievementDataService.cs" };
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find AchievementDataService.cs.");
            return null;
        }
    }
}
