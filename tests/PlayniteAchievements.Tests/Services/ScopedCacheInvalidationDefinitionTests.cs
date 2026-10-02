using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// A custom-data edit raises a scoped CacheInvalidated naming one game. Two subscribers
    /// ignored that scope and did library-wide work anyway: the plugin invalidated the start
    /// page immediately (making every live widget re-pull a full library snapshot, once per
    /// edit, bypassing the coalescer the CustomDataChanged path uses), and the theme service
    /// took the base EventArgs so it could not see the scope at all -- repainting the
    /// fullscreen surface and invalidating friends data for an edit to an unrelated game.
    ///
    /// Neither class is constructible here without a full plugin graph, so the wiring is
    /// asserted against the source, the way the other definition tests in this folder are.
    /// </summary>
    [TestClass]
    public class ScopedCacheInvalidationDefinitionTests
    {
        [TestMethod]
        public void ScopedArgs_NameTheChangedGamesAndCollapseWhenThereAreTooMany()
        {
            var one = CacheInvalidatedEventArgs.Scoped(new[] { Guid.NewGuid() });
            Assert.IsFalse(one.IsFull, "One named game is a scoped change.");
            Assert.AreEqual(1, one.ChangedGameIds.Count);

            var tooMany = CacheInvalidatedEventArgs.Scoped(
                Enumerable.Range(0, CacheInvalidatedEventArgs.MaxScopedGames + 1)
                    .Select(_ => Guid.NewGuid())
                    .ToList());
            Assert.IsTrue(
                tooMany.IsFull,
                "Past the cap a scoped invalidation stops paying for itself, so senders collapse " +
                "to a full one and consumers must take their wholesale path.");
            Assert.AreEqual(0, tooMany.ChangedGameIds.Count);

            Assert.IsTrue(CacheInvalidatedEventArgs.Scoped(null).IsFull);
            Assert.IsTrue(CacheInvalidatedEventArgs.Scoped(Array.Empty<Guid>()).IsFull);
        }

        [TestMethod]
        public void ThePluginHandler_CoalescesTheStartPageOnScopedInvalidationsOnly()
        {
            var body = Between(
                ReadPlugin(),
                "_cacheManager.CacheInvalidated +=",
                "_cacheManager.CacheDeltaUpdated +=");

            Assert.IsFalse(
                body.Contains("(_, __) =>"),
                "The handler must bind the args, or the scope it carries is invisible.");

            StringAssert.Contains(
                body,
                "args?.IsFull == false",
                "Scope is what decides between the coalescer and the immediate path.");
            StringAssert.Contains(
                body,
                "ScheduleStartPageInvalidate()",
                "A burst of per-game edits must collapse; each start-page invalidation makes " +
                "every live widget rebuild a full library snapshot.");
            StringAssert.Contains(
                body,
                "InvalidateStartPageData()",
                "A full invalidation is a bulk event, not a burst, and still lands immediately.");

            // Friend coordinators stay unconditional: scoped raises also come from the refresh
            // pipeline's end-of-run raise, which includes friend-mode runs.
            StringAssert.Contains(body, "InvalidateFriendDataCoordinators()");
        }

        [TestMethod]
        public void TheThemeHandler_TakesTheTypedArgsAndHonoursTheScope()
        {
            var source = ReadThemeService();

            StringAssert.Contains(
                source,
                "RefreshService_CacheInvalidated(object sender, CacheInvalidatedEventArgs e)",
                "Typed, or IsFull and ChangedGameIds are invisible.");

            var body = Between(
                source,
                "private void RefreshService_CacheInvalidated(",
                "private void FriendCache_FriendCacheInvalidated(");

            StringAssert.Contains(body, "e?.IsFull == false");
            StringAssert.Contains(
                body,
                "e.ChangedGameIds.Contains(selectedGameId.Value)",
                "A scoped change may only repaint the selected-game surface when it actually " +
                "touched that game.");
            StringAssert.Contains(
                body,
                "if (!isScoped)",
                "A current-user achievement edit cannot move friend data, so the friends " +
                "coordinator is invalidated only on a full change.");
        }

        [TestMethod]
        public void TheThemeHandler_StillDoesTheWholeLibraryWorkOnAFullInvalidation()
        {
            var body = Between(
                ReadThemeService(),
                "private void RefreshService_CacheInvalidated(",
                "private void FriendCache_FriendCacheInvalidated(");

            // !isScoped short-circuits the scope test, so a full invalidation keeps every
            // behaviour it had before the scope was honoured. Losing this turns a bulk refresh
            // into a no-op for the theme surface.
            StringAssert.Contains(body, "!isScoped ||");
            StringAssert.Contains(body, "RequestRefresh()");
            StringAssert.Contains(body, "RequestUpdate(selectedGameId)");
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadPlugin()
        {
            return ReadRepoFile("source", "PlayniteAchievementsPlugin.cs");
        }

        private static string ReadThemeService()
        {
            return ReadRepoFile(
                "source", "Services", "ThemeIntegration", "ThemeIntegrationService.cs");
        }

        private static string ReadRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {parts.Last()}.");
            return null;
        }
    }
}
