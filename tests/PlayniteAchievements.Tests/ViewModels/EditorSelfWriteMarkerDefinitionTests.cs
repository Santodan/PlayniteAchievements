using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The editor marks its own writes so the refresh cascade they set off does not make it
    /// rebuild rows it already shows. That marker was cleared by the first reader, but one write
    /// fans out into two refresh legs -- a cache-updated path reaching HandleStateChanged and the
    /// revision-changed path reaching HandleCustomDataRevisionChanged. The first leg consumed the
    /// marker and the second mistook the edit for an external change, running a full ReloadData:
    /// 641 rows rebuilt, the grid reset, every visible container re-realized. Measured at about a
    /// second of UI-thread work starting ~14ms after the save, which is the per-edit hitch.
    ///
    /// ManageAchievementsControl is not linked into this project, so these assert against the
    /// source the way the other wiring definition tests here do.
    /// </summary>
    [TestClass]
    public class EditorSelfWriteMarkerDefinitionTests
    {
        [TestMethod]
        public void TheMarker_IsNotClearedByTheFirstReader()
        {
            var body = Between(
                ReadControl(),
                "private bool ConsumeEditorSelfWrite()",
                "private void ScheduleSelfWriteMarkerClear()");

            Assert.IsFalse(
                body.Contains("SuppressExternalRefresh = false"),
                "Clearing on read is the regression: the second leg of the same write's cascade " +
                "then sees no marker and rebuilds every row.");
            StringAssert.Contains(body, "ScheduleSelfWriteMarkerClear()");
        }

        [TestMethod]
        public void TheMarker_IsClearedAfterTheCascadeRatherThanOnAWallClockTimer()
        {
            var body = Between(
                ReadControl(),
                "private void ScheduleSelfWriteMarkerClear()",
                "private void HandleStateChanged(");

            // Background priority runs after every synchronous leg of the write's cascade, so the
            // suppression is scoped to that cascade. A time-based window would be a TTL, which
            // this codebase deliberately avoids for refresh decisions.
            StringAssert.Contains(body, "DispatcherPriority.Background");
            StringAssert.Contains(
                body,
                "SuppressExternalRefresh = false",
                "The marker must still be cleared, or a genuinely external change stops " +
                "refreshing the editor entirely.");

            Assert.IsFalse(
                body.Contains("DispatcherTimer") || body.Contains("TimeSpan.From"),
                "Scope the suppression to the cascade, not to a wall-clock window.");
        }

        [TestMethod]
        public void TheClear_IsQueuedOnlyOnce_PerCascade()
        {
            var body = Between(
                ReadControl(),
                "private void ScheduleSelfWriteMarkerClear()",
                "private void HandleStateChanged(");

            // Both legs call Consume, so without the guard the second would queue a second clear
            // and the marker could be dropped mid-cascade by the first callback.
            StringAssert.Contains(body, "_selfWriteMarkerClearQueued");
            StringAssert.Contains(body, "return;");
        }

        [TestMethod]
        public void BothRefreshLegs_StillAskWhetherTheWriteWasTheirOwn()
        {
            var source = ReadControl();

            // If either leg stops consulting the marker, the editor either rebuilds on its own
            // write again or stops refreshing on a real external change.
            Assert.AreEqual(
                2,
                CountOccurrences(source, "!ConsumeEditorSelfWrite()"),
                "Exactly the two legs -- HandleStateChanged and HandleCustomDataRevisionChanged " +
                "-- decide the editor refresh from this marker.");
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

        private static string ReadControl()
        {
            var parts = new[]
            {
                "source", "Views", "ManageAchievements", "ManageAchievementsControl.xaml.cs"
            };

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

            Assert.Fail("Could not find ManageAchievementsControl.xaml.cs.");
            return null;
        }
    }
}
