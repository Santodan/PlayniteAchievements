using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class PlayniteGameMetadataFormatterTests
    {
        // Hour formatting depends on the plugin formatting culture; pin it for determinism.
        [TestInitialize]
        public void PinFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        [TestCleanup]
        public void RestoreFormattingCulture()
        {
            FormattingCulture.Initialize(() => "english");
        }

        [TestMethod]
        public void FormatPlaytime_ReturnsEmptyForZeroPlaytime()
        {
            Assert.AreEqual(string.Empty, PlayniteGameMetadataFormatter.FormatPlaytime(0));
        }

        [TestMethod]
        public void FormatPlaytime_ReturnsEmptyForSubMinutePlaytime()
        {
            Assert.AreEqual(string.Empty, PlayniteGameMetadataFormatter.FormatPlaytime(30));
        }

        [TestMethod]
        public void FormatPlaytime_FormatsSubHourPlaytimeAsLessThanOneHour()
        {
            Assert.AreEqual(
                "<1h",
                PlayniteGameMetadataFormatter.FormatPlaytime(59UL * 60, PlaytimeDisplayMode.HoursAndMinutes));
        }

        [TestMethod]
        public void FormatPlaytime_FormatsHoursOnly()
        {
            Assert.AreEqual(
                "4h",
                PlayniteGameMetadataFormatter.FormatPlaytime(4UL * 60 * 60, PlaytimeDisplayMode.HoursAndMinutes));
        }

        [TestMethod]
        public void FormatPlaytime_FormatsHoursAndMinutes()
        {
            var playtimeSeconds = ((125UL * 60) + 28UL) * 60;
            Assert.AreEqual(
                "125h28m",
                PlayniteGameMetadataFormatter.FormatPlaytime(playtimeSeconds, PlaytimeDisplayMode.HoursAndMinutes));
        }

        [TestMethod]
        public void FormatPlaytime_HoursOnlyMode_DropsMinutes()
        {
            var playtimeSeconds = ((125UL * 60) + 28UL) * 60;
            Assert.AreEqual(
                "125h",
                PlayniteGameMetadataFormatter.FormatPlaytime(playtimeSeconds, PlaytimeDisplayMode.HoursOnly));
        }

        [TestMethod]
        public void FormatPlaytime_HoursOnlyMode_FormatsSubHourPlaytimeAsLessThanOneHour()
        {
            Assert.AreEqual(
                "<1h",
                PlayniteGameMetadataFormatter.FormatPlaytime(15UL * 60, PlaytimeDisplayMode.HoursOnly));
        }

        [TestMethod]
        public void FormatPlaytime_UsesThousandsSeparatorForHours()
        {
            var playtimeSeconds = 1234UL * 60 * 60;
            Assert.AreEqual(
                "1,234h",
                PlayniteGameMetadataFormatter.FormatPlaytime(playtimeSeconds, PlaytimeDisplayMode.HoursOnly));

            FormattingCulture.Initialize(() => "german");
            try
            {
                Assert.AreEqual(
                    "1.234h",
                    PlayniteGameMetadataFormatter.FormatPlaytime(playtimeSeconds, PlaytimeDisplayMode.HoursOnly));
            }
            finally
            {
                FormattingCulture.Initialize(() => "english");
            }
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
        public void BuildOverviewMetadataText_JoinsAllSegments()
        {
            Assert.AreEqual(
                "PlayStation 3 • 125h28m • Japan",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText("PlayStation 3", "125h28m", "Japan"));
        }

        [TestMethod]
        public void BuildOverviewMetadataText_ReturnsPlaytimeOnlyWhenOtherSegmentsMissing()
        {
            Assert.AreEqual(
                "59m",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(string.Empty, "59m", string.Empty));
        }

        [TestMethod]
        public void BuildOverviewMetadataText_DropsZeroPlaytimeSegment()
        {
            Assert.AreEqual(
                "PlayStation 3 • Japan",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText("PlayStation 3", "0h", "Japan"));
            Assert.AreEqual(
                string.Empty,
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(string.Empty, "0m", string.Empty));
        }

        [TestMethod]
        public void IsZeroPlaytimeText_DetectsZeroDurationsAcrossUnitFormats()
        {
            Assert.IsTrue(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("0m"));
            Assert.IsTrue(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("0h"));
            Assert.IsTrue(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("0h0m"));
            Assert.IsTrue(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("0 hours"));
            Assert.IsFalse(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("0h30m"));
            Assert.IsFalse(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("10h"));
            Assert.IsFalse(PlayniteGameMetadataFormatter.IsZeroPlaytimeText(string.Empty));
            Assert.IsFalse(PlayniteGameMetadataFormatter.IsZeroPlaytimeText("hours"));
        }

        [TestMethod]
        public void BuildOverviewMetadataText_OmitsMissingPlatformOrRegion()
        {
            Assert.AreEqual(
                "PlayStation 3 • 4h",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText("PlayStation 3", "4h", string.Empty));
            Assert.AreEqual(
                "4h • Japan",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(string.Empty, "4h", "Japan"));
        }

        [TestMethod]
        public void BuildOverviewMetadataText_WithFlags_IncludesOnlyEnabledSegments()
        {
            Assert.AreEqual(
                "PlayStation 3 • Japan",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(
                    "PlayStation 3", "125h28m", "Japan",
                    showPlatform: true, showPlaytime: false, showRegion: true));

            Assert.AreEqual(
                "125h28m",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(
                    "PlayStation 3", "125h28m", "Japan",
                    showPlatform: false, showPlaytime: true, showRegion: false));
        }

        [TestMethod]
        public void BuildOverviewMetadataText_WithAllFlagsDisabled_ReturnsEmpty()
        {
            Assert.AreEqual(
                string.Empty,
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(
                    "PlayStation 3", "125h28m", "Japan",
                    showPlatform: false, showPlaytime: false, showRegion: false));
        }

        [TestMethod]
        public void BuildOverviewMetadataText_WithFlagEnabledButSegmentEmpty_OmitsSegment()
        {
            Assert.AreEqual(
                "PlayStation 3",
                PlayniteGameMetadataFormatter.BuildOverviewMetadataText(
                    "PlayStation 3", string.Empty, string.Empty,
                    showPlatform: true, showPlaytime: true, showRegion: true));
        }
    }
}
