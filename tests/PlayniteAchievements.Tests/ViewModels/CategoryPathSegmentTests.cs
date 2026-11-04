using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class CategoryPathSegmentTests
    {
        [TestMethod]
        public void Build_IsEmptyWhenNothingIsDrilled()
        {
            Assert.AreEqual(0, CategoryPathSegment.Build(null).Count);
            Assert.AreEqual(0, CategoryPathSegment.Build(Array.Empty<string>()).Count);
        }

        [TestMethod]
        public void Build_SingleLevelHasNoMarker()
        {
            var segments = CategoryPathSegment.Build(new[] { "DLC" });

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual("DLC", segments[0].Content);
            Assert.AreEqual(1, segments[0].Depth);
            Assert.IsTrue(segments[0].IsCurrent);
            Assert.IsFalse(segments[0].ShowSeparator);
        }

        [TestMethod]
        public void Build_NestedPathLeadsWithTheMarkerAndEndsOnTheLeaf()
        {
            var segments = CategoryPathSegment.Build(new[] { "DLC", "Winter", "Frost" });

            CollectionAssert.AreEqual(
                new[] { "…", "DLC", "Winter", "Frost" },
                segments.Select(segment => segment.Content).ToList());
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                segments.Select(segment => segment.Depth).ToList());
            Assert.IsTrue(segments.Last().IsCurrent);
            Assert.IsTrue(segments.Take(3).All(segment => segment.ShowSeparator));
            Assert.AreEqual("DLC > Winter", segments[0].ToolTip);
            Assert.IsTrue(segments.All(segment => !segment.IsNavigable));
        }
    }
}
