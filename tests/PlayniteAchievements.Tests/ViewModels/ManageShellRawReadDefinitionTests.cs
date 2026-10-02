using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// ReloadCore loaded both the hydrated and the raw game snapshot on every reload, so each
    /// one cost two cold reads on the UI thread. The raw copy has exactly one consumer -- the
    /// AchievementPageLinkContext below -- and that consumer cannot observe it while the
    /// hydrated copy is present.
    ///
    /// The first test pins the substitutability the optimization rests on; the second pins that
    /// the call site actually takes advantage of it. ManageAchievementsViewModel is not linked
    /// into this project, so the second asserts against the source.
    /// </summary>
    [TestClass]
    public class ManageShellRawReadDefinitionTests
    {
        [TestMethod]
        public void BestGameData_IgnoresTheRawCopyWheneverTheHydratedOneIsPresent()
        {
            var hydrated = new GameAchievementData { ProviderKey = "steam", AppId = 42 };
            var raw = new GameAchievementData { ProviderKey = "steam", AppId = 99 };

            var withRaw = new AchievementPageLinkContext(null, hydrated, raw, null);
            var withoutRaw = new AchievementPageLinkContext(null, hydrated, null, null);

            Assert.AreSame(
                hydrated,
                withRaw.BestGameData,
                "BestGameData is `GameData ?? RawGameData`, so a present hydrated copy wins.");
            Assert.AreSame(
                withRaw.BestGameData,
                withoutRaw.BestGameData,
                "Passing null for the raw copy must be indistinguishable to every consumer once " +
                "the hydrated copy is present. This is what lets ReloadCore skip the second read.");
        }

        [TestMethod]
        public void BestGameData_StillFallsBackToTheRawCopyWhenNothingIsHydrated()
        {
            var raw = new GameAchievementData { ProviderKey = "gog", AppId = 7 };

            var context = new AchievementPageLinkContext(null, null, raw, null);

            Assert.AreSame(
                raw,
                context.BestGameData,
                "The fallback is the whole reason the raw read exists; skipping it when there is " +
                "no hydrated copy would drop the achievement-page link for uncached games.");
        }

        [TestMethod]
        public void ReloadCore_ReadsTheRawSnapshotOnlyWhenTheHydratedOneIsMissing()
        {
            var body = Between(
                ReadViewModel(),
                "private void ReloadCore()",
                "private void OpenAchievements()");

            StringAssert.Contains(
                body,
                "gameData != null ? null : GetRawGameData()",
                "An unconditional GetRawGameData() costs a second cold snapshot load on the UI " +
                "thread for a value no consumer can reach.");

            // Guard against the conditional being written but the eager call left behind.
            Assert.AreEqual(
                1,
                CountOccurrences(body, "GetRawGameData()"),
                "ReloadCore should reach for the raw snapshot in exactly one place.");
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

        private static string ReadViewModel()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            var parts = new[]
            {
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsViewModel.cs"
            };

            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find ManageAchievementsViewModel.cs.");
            return null;
        }
    }
}
