using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class PlayniteGameMetadataFormatterTests
    {
        [TestMethod]
        public void FormatPlaytime_FormatsZeroMinutes()
        {
            Assert.AreEqual("0m", PlayniteGameMetadataFormatter.FormatPlaytime(0));
        }

        [TestMethod]
        public void FormatPlaytime_FormatsMinutesOnly()
        {
            Assert.AreEqual("59m", PlayniteGameMetadataFormatter.FormatPlaytime(59UL * 60));
        }

        [TestMethod]
        public void FormatPlaytime_FormatsHoursOnly()
        {
            Assert.AreEqual("4h", PlayniteGameMetadataFormatter.FormatPlaytime(4UL * 60 * 60));
        }

        [TestMethod]
        public void FormatPlaytime_FormatsHoursAndMinutes()
        {
            var playtimeSeconds = ((125UL * 60) + 28UL) * 60;
            Assert.AreEqual("125h28m", PlayniteGameMetadataFormatter.FormatPlaytime(playtimeSeconds));
        }

        [TestMethod]
        public void JoinDisplayNames_ReturnsEmptyForMissingValues()
        {
            Assert.AreEqual(string.Empty, PlayniteGameMetadataFormatter.JoinDisplayNames(null));
            Assert.AreEqual(
                string.Empty,
                PlayniteGameMetadataFormatter.JoinDisplayNames(new[] { "", "   ", null }));
        }

        [TestMethod]
        public void JoinDisplayNames_ReturnsSingleNormalizedValue()
        {
            Assert.AreEqual(
                "PlayStation 3",
                PlayniteGameMetadataFormatter.JoinDisplayNames(new[] { "  PlayStation 3  " }));
        }

        [TestMethod]
        public void JoinDisplayNames_DedupesAndJoinsMultipleValues()
        {
            Assert.AreEqual(
                "PlayStation 3, Vita, Japan",
                PlayniteGameMetadataFormatter.JoinDisplayNames(new[] { "PlayStation 3", "Vita", "playstation 3", " ", "Japan" }));
        }

        [TestMethod]
        public void BuildSidebarMetadataText_JoinsAllSegments()
        {
            Assert.AreEqual(
                "PlayStation 3 • 125h28m • Japan",
                PlayniteGameMetadataFormatter.BuildSidebarMetadataText("PlayStation 3", "125h28m", "Japan"));
        }

        [TestMethod]
        public void BuildSidebarMetadataText_ReturnsPlaytimeOnlyWhenOtherSegmentsMissing()
        {
            Assert.AreEqual(
                "0m",
                PlayniteGameMetadataFormatter.BuildSidebarMetadataText(string.Empty, "0m", string.Empty));
        }

        [TestMethod]
        public void BuildSidebarMetadataText_OmitsMissingPlatformOrRegion()
        {
            Assert.AreEqual(
                "PlayStation 3 • 4h",
                PlayniteGameMetadataFormatter.BuildSidebarMetadataText("PlayStation 3", "4h", string.Empty));
            Assert.AreEqual(
                "4h • Japan",
                PlayniteGameMetadataFormatter.BuildSidebarMetadataText(string.Empty, "4h", "Japan"));
        }
    }
}
