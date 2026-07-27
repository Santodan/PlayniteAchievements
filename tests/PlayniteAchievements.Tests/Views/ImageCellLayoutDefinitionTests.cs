using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Locks the image cell layout contract: art cells are width-driven
    /// (the Uniform image fills the column width so interactive column
    /// resizing grows the art) and rely on a configured fixed row height
    /// bounding the layout for containment. No explicit size clamp may
    /// reintroduce a height-driven cap on auto-sized rows.
    /// </summary>
    [TestClass]
    public class ImageCellLayoutDefinitionTests
    {
        [TestMethod]
        public void AchievementCellTemplates_ArtCells_AreWidthDrivenUniformFit()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Resources", "AchievementCellTemplates.xaml"));

            StringAssert.Contains(xaml, "x:Key=\"AchievementGameColumnTemplate\"");
            StringAssert.Contains(xaml, "x:Key=\"AchievementCategoryColumnTemplate\"");
            StringAssert.Contains(xaml, "Stretch=\"Uniform\"");

            // Art cells must not clamp their height to the row-height setting;
            // width drives sizing on auto rows and the fixed row height bounds
            // the layout when configured.
            Assert.IsFalse(
                xaml.Contains("MaxHeight=\"{Binding FixedRowHeight"),
                "art cell templates must not bind MaxHeight to FixedRowHeight");

            // The legacy converter-driven explicit sizing must not return.
            Assert.IsFalse(
                xaml.Contains("ImageFitDimension}\" ConverterParameter=\"icon") ||
                xaml.Contains("ImageFitDimension}\" ConverterParameter=\"cover") ||
                xaml.Contains("ImageFitDimension}\" ConverterParameter=\"auto"),
                "art cells must not size via ImageFitDimension icon/cover/auto modes");
        }

        [TestMethod]
        public void SummaryGrids_ArtCells_AreWidthDrivenUniformFit()
        {
            foreach (var name in new[] { "GameSummariesGridControl.xaml", "FriendSummariesGridControl.xaml" })
            {
                var xaml = File.ReadAllText(FindRepoFile("source", "Views", "Controls", name));

                Assert.IsFalse(
                    xaml.Contains("MaxHeight=\"{Binding FixedRowHeight"),
                    name + " must not bind image MaxHeight to FixedRowHeight");
                Assert.IsFalse(
                    xaml.Contains("ImageFitDimension}\" ConverterParameter=\"icon") ||
                    xaml.Contains("ImageFitDimension}\" ConverterParameter=\"cover") ||
                    xaml.Contains("ImageFitDimension}\" ConverterParameter=\"auto"),
                    name + " must not size art via ImageFitDimension icon/cover/auto modes");
            }
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

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
