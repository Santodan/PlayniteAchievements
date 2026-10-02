using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// A per-achievement override is stored twice: on the override record, and in the legacy
    /// per-field maps beside it. Normalization keeps them in step by merging the maps into the
    /// record and then republishing them from it - in that order.
    ///
    /// That order is why a writer must not leave a stale map behind. Clearing a value on the
    /// record while its map still holds the old one means the next save merges the old one back,
    /// and the change is silently undone. That is what stopped an icon override from ever being
    /// cleared: the write reported no entries and the path was back on the next load.
    ///
    /// The service is not linked into this project, so the contract is pinned against its source.
    /// </summary>
    [TestClass]
    public class AchievementOverrideMirrorTests
    {
        [TestMethod]
        public void StoringOverrides_DropsTheLegacyMirrorMaps()
        {
            var body = ExtractMethod(ReadService(), "private static void StoreOverrides(");

            foreach (var mirror in new[]
            {
                "AchievementCategoryOverrides",
                "AchievementCategoryTypeOverrides",
                "AchievementNotes",
                "AchievementUnlockedIconOverrides",
                "AchievementLockedIconOverrides"
            })
            {
                StringAssert.Contains(
                    body,
                    "customData." + mirror + " = null;",
                    $"{mirror} has to be dropped when the record is stored. Left in place, it " +
                    "folds a cleared value back in on the next save.");
            }
        }

        [TestMethod]
        public void TheNormalizer_StillMergesBeforeItRepublishes()
        {
            // The reason the above matters. If this order ever changes - republish first, or drop
            // the merge - the mirrors can no longer resurrect anything and the drop above becomes
            // belt and braces rather than load bearing. Worth knowing either way.
            var normalizer = ReadNormalizer();

            var mergeAt = normalizer.IndexOf("MergeLegacyAchievementMaps", StringComparison.Ordinal);
            var projectAt = normalizer.IndexOf(
                "AchievementUnlockedIconOverrides = ProjectOverrideField",
                StringComparison.Ordinal);

            Assert.IsTrue(mergeAt > 0, "The legacy merge is what makes a stale mirror dangerous.");
            Assert.IsTrue(projectAt > 0, "The mirrors are republished from the record.");
            Assert.IsTrue(
                mergeAt < projectAt,
                "The merge runs before the republish, so a mirror the writer left behind wins " +
                "over a value the record just cleared.");
        }

        private static string ExtractMethod(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find {signature}.");

            var open = source.IndexOf('{', start);
            Assert.IsTrue(open > start, $"{signature} has no body.");

            var depth = 0;
            for (var index = open; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, index - open + 1);
                    }
                }
            }

            Assert.Fail($"{signature} does not close.");
            return null;
        }

        private static string ReadService()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Services", "Achievements", "AchievementOverridesService.cs"));
        }

        private static string ReadNormalizer()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Services", "GameCustomData", "GameCustomDataNormalizer.cs"));
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
