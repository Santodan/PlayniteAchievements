using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The projection service subscribed one handler, typed (object, EventArgs), to both
    /// CacheInvalidated and CacheDeltaUpdated. It bound to either event precisely because it took
    /// the base class -- and so could not read the scope either one carries, which made every
    /// single-game change schedule a whole-library rebuild.
    ///
    /// LibraryProjectionService is not linked into this project, so these assert against the
    /// source the way the other wiring definition tests here do.
    /// </summary>
    [TestClass]
    public class LibraryProjectionScopeDefinitionTests
    {
        [TestMethod]
        public void BothEvents_AreHandledWithTheirOwnArgumentType()
        {
            var source = ReadService();

            StringAssert.Contains(
                source,
                "OnCacheInvalidatedForProjection(object sender, CacheInvalidatedEventArgs e)",
                "Typed, or IsFull and ChangedGameIds are invisible.");
            StringAssert.Contains(
                source,
                "OnCacheDeltaForProjection(object sender, CacheDeltaEventArgs e)",
                "Typed, or Key and IsFullReset are invisible.");

            // Both subscriptions must move. One refresh write raises the delta as well as the
            // invalidation, so leaving either on the unconditional path would keep warming and
            // the change would achieve nothing.
            StringAssert.Contains(source, "CacheInvalidated += OnCacheInvalidatedForProjection");
            StringAssert.Contains(source, "CacheDeltaUpdated += OnCacheDeltaForProjection");
            StringAssert.Contains(source, "CacheInvalidated -= OnCacheInvalidatedForProjection");
            StringAssert.Contains(source, "CacheDeltaUpdated -= OnCacheDeltaForProjection");

            Assert.IsFalse(
                source.Contains("OnProjectionSourceChanged"),
                "The untyped handler must be gone, not merely bypassed.");
        }

        [TestMethod]
        public void AScopedChange_StillClearsTheCacheAndOnlyDefersThePrecompute()
        {
            var source = ReadService();
            var core = Between(source, "private void InvalidateCore(", "private void ScheduleWarm(");

            // Clearing is unconditional: that is what keeps a consumer from being served a
            // projection built before the change it should reflect.
            StringAssert.Contains(core, "_cacheGeneration++;");
            StringAssert.Contains(core, "_cache.Clear();");
            Assert.IsFalse(
                core.Contains("if (scopedChange)"),
                "The clear must not be conditional on the scope -- only the warm delay is.");
            StringAssert.Contains(
                core,
                "ScheduleWarm(scopedChange ? ScopedWarmIdleDelay : TimeSpan.Zero)",
                "A scoped change waits for the library to go quiet; a full one warms as before.");
        }

        [TestMethod]
        public void TheScopedWarmDelay_OutlastsTheRoutineRefreshCadence()
        {
            var source = ReadService();

            StringAssert.Contains(
                source,
                "ScopedWarmIdleDelay = TimeSpan.FromSeconds(20)",
                "It has to exceed the ~15s in-game refresh cadence, or a steady stream of " +
                "single-game changes still triggers a whole-library rebuild between them.");
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
            var parts = new[] { "source", "Services", "Library", "LibraryProjectionService.cs" };
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find LibraryProjectionService.cs.");
            return null;
        }
    }
}
