using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The Manage window reloaded its shell synchronously on every custom-data edit, and that
    /// reload re-read the game's snapshot on the UI thread -- 369ms per edit in a captured
    /// session, because the snapshot had just been invalidated by the same write.
    ///
    /// Deferring it past the revision bump lets the visible tab's own rehydration warm the
    /// snapshot first. These pin the properties that make the deferral safe rather than merely
    /// faster; ManageAchievementsViewModel is not linked into this project, so they assert
    /// against the source.
    /// </summary>
    [TestClass]
    public class ShellReloadDeferralDefinitionTests
    {
        [TestMethod]
        public void ThePendingReload_IsFlushedWhenTheWindowCloses()
        {
            var source = ReadRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsControl.xaml.cs");

            StringAssert.Contains(
                source,
                "FlushPendingShellReload()",
                "A deferred reload dropped on close would leave the view model's state behind " +
                "the last edit. This is the same force-flush the icon debounce already does.");
        }

        [TestMethod]
        public void TheDeferral_FallsBackToASynchronousReloadWithoutADispatcher()
        {
            var body = Between(
                ReadViewModel(),
                "internal void ScheduleShellReload()",
                "private void ShellReloadTimer_Tick(");

            StringAssert.Contains(body, "dispatcher == null || !dispatcher.CheckAccess()");
            StringAssert.Contains(
                body,
                "Reload();",
                "Off the UI thread there is no timer to defer onto, and silently dropping the " +
                "reload would lose the update entirely.");
        }

        [TestMethod]
        public void TheDeferral_IsTrailingEdgeSoABurstCollapses()
        {
            var body = Between(
                ReadViewModel(),
                "internal void ScheduleShellReload()",
                "private void ShellReloadTimer_Tick(");

            // Stop-then-start is what pushes the deadline out on each edit. Without the Stop the
            // timer keeps its original deadline and fires mid-burst.
            var stop = body.IndexOf("_shellReloadTimer.Stop();", StringComparison.Ordinal);
            var start = body.IndexOf("_shellReloadTimer.Start();", StringComparison.Ordinal);
            Assert.IsTrue(stop >= 0 && start > stop, "The timer must be restarted, not just started.");

            StringAssert.Contains(
                ReadViewModel(),
                "DispatcherPriority.Background",
                "Background priority keeps the reload behind input and rendering, which is the " +
                "point of deferring it at all.");
        }

        [TestMethod]
        public void TheTick_StopsTheTimerSoItDoesNotRepeat()
        {
            var body = Between(
                ReadViewModel(),
                "private void ShellReloadTimer_Tick(",
                "internal void FlushPendingShellReload()");

            // A DispatcherTimer repeats until stopped; leaving it running would reload the shell
            // every 150ms for the life of the window.
            StringAssert.Contains(body, "_shellReloadTimer?.Stop();");
            StringAssert.Contains(body, "Reload();");
        }

        [TestMethod]
        public void TheFlush_OnlyReloadsWhenSomethingIsActuallyPending()
        {
            var body = Between(
                ReadViewModel(),
                "internal void FlushPendingShellReload()",
                "internal void NotifyIconOverridesChanged(");

            StringAssert.Contains(
                body,
                "IsEnabled != true",
                "Closing a window with no pending edit must not pay for a reload.");
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadViewModel()
        {
            return ReadRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsViewModel.cs");
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
