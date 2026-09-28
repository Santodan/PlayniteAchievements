using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class TimeWindowTests
    {
        private static readonly DateTime Today = new DateTime(2026, 9, 28);

        [TestMethod]
        public void SevenDays_CoversTodayAndSixPriorDays()
        {
            var range = TimeWindow.FromPreset(TimelineRange.SevenDays).Resolve(Today, null);

            Assert.AreEqual(new DateTime(2026, 9, 22), range.Start);
            Assert.AreEqual(Today, range.End);
            Assert.AreEqual(7, range.DayCount);
        }

        [TestMethod]
        public void FourteenDays_StillResolvesForLegacySettings()
        {
            var range = TimeWindow.FromPreset(TimelineRange.FourteenDays).Resolve(Today, null);

            Assert.AreEqual(14, range.DayCount);
            Assert.IsFalse(TimeWindow.Presets.Contains(TimelineRange.FourteenDays));
        }

        [DataTestMethod]
        [DataRow(2026, 3, 31, 2026, 3, 1)]
        [DataRow(2026, 3, 30, 2026, 3, 1)]
        [DataRow(2026, 1, 31, 2026, 1, 1)]
        [DataRow(2026, 3, 1, 2026, 2, 2)]
        public void OneMonth_StartsTheDayAfterSameDayLastMonth(int y, int m, int d, int ey, int em, int ed)
        {
            var range = TimeWindow.FromPreset(TimelineRange.OneMonth).Resolve(new DateTime(y, m, d), null);

            Assert.AreEqual(new DateTime(ey, em, ed), range.Start);
        }

        [TestMethod]
        public void SixMonths_IsAPresetChipAndStartsTheDayAfterSixMonthsAgo()
        {
            Assert.IsTrue(TimeWindow.Presets.Contains(TimelineRange.SixMonths));

            var range = TimeWindow.FromPreset(TimelineRange.SixMonths).Resolve(Today, null);

            Assert.AreEqual(new DateTime(2026, 3, 29), range.Start);
            Assert.AreEqual(Today, range.End);
            Assert.IsTrue(TimeWindow.TryParse("SixMonths", out var parsed));
            Assert.AreEqual(TimelineRange.SixMonths, parsed.Preset);
        }

        [TestMethod]
        public void ThreeMonths_FromMayThirtyFirst_StartsMarchFirst()
        {
            var range = TimeWindow.FromPreset(TimelineRange.ThreeMonths).Resolve(new DateTime(2026, 5, 31), null);

            Assert.AreEqual(new DateTime(2026, 3, 1), range.Start);
        }

        [TestMethod]
        public void OneYear_FromLeapDay_Covers366Days()
        {
            var range = TimeWindow.FromPreset(TimelineRange.OneYear).Resolve(new DateTime(2024, 2, 29), null);

            Assert.AreEqual(new DateTime(2023, 3, 1), range.Start);
            Assert.AreEqual(366, range.DayCount);
        }

        [TestMethod]
        public void OneYear_FromFebTwentyEighth_StartsOnPriorLeapDay()
        {
            var range = TimeWindow.FromPreset(TimelineRange.OneYear).Resolve(new DateTime(2025, 2, 28), null);

            Assert.AreEqual(new DateTime(2024, 2, 29), range.Start);
        }

        [DataTestMethod]
        [DataRow(2026, 3, 8)]
        [DataRow(2026, 11, 1)]
        public void SevenDays_OnDstTransitionDay_KeepsMidnightKeys(int y, int m, int d)
        {
            var today = new DateTime(y, m, d, 15, 30, 0, DateTimeKind.Local);
            var range = TimeWindow.FromPreset(TimelineRange.SevenDays).Resolve(today, null);

            Assert.AreEqual(7, range.DayCount);
            for (var day = range.Start; day <= range.End; day = day.AddDays(1))
            {
                Assert.AreEqual(TimeSpan.Zero, day.TimeOfDay);
            }
        }

        [TestMethod]
        public void All_UsesEarliestData()
        {
            var earliest = new DateTime(2019, 6, 3, 12, 0, 0);
            var range = TimeWindow.All.Resolve(Today, earliest);

            Assert.AreEqual(earliest.Date, range.Start);
            Assert.AreEqual(Today, range.End);
        }

        [TestMethod]
        public void All_WithoutData_IsSingleDay()
        {
            var range = TimeWindow.All.Resolve(Today, null);

            Assert.AreEqual(Today, range.Start);
            Assert.AreEqual(1, range.DayCount);
        }

        [TestMethod]
        public void All_EarliestAfterToday_ClampsToToday()
        {
            var range = TimeWindow.All.Resolve(Today, Today.AddDays(5));

            Assert.AreEqual(Today, range.Start);
            Assert.AreEqual(Today, range.End);
        }

        [TestMethod]
        public void Custom_BothBounds_ResolveExactly()
        {
            var window = TimeWindow.Custom(new DateTime(2025, 1, 1, 8, 0, 0), new DateTime(2025, 6, 30, 23, 0, 0));
            var range = window.Resolve(Today, new DateTime(2010, 1, 1));

            Assert.AreEqual(new DateTime(2025, 1, 1), range.Start);
            Assert.AreEqual(new DateTime(2025, 6, 30), range.End);
            Assert.IsTrue(window.IsCustom);
            Assert.IsFalse(window.IsRolling);
        }

        [TestMethod]
        public void Custom_FromOnly_EndsToday()
        {
            var window = TimeWindow.Custom(new DateTime(2026, 9, 1), null);
            var range = window.Resolve(Today, null);

            Assert.IsTrue(window.IsRolling);
            Assert.AreEqual(new DateTime(2026, 9, 1), range.Start);
            Assert.AreEqual(Today, range.End);
        }

        [TestMethod]
        public void Custom_ToOnly_StartsAtEarliestData_OrAtToWithoutData()
        {
            var window = TimeWindow.Custom(null, new DateTime(2026, 6, 30));

            var withData = window.Resolve(Today, new DateTime(2020, 2, 2));
            Assert.AreEqual(new DateTime(2020, 2, 2), withData.Start);
            Assert.AreEqual(new DateTime(2026, 6, 30), withData.End);

            var withoutData = window.Resolve(Today, null);
            Assert.AreEqual(new DateTime(2026, 6, 30), withoutData.Start);
            Assert.AreEqual(1, withoutData.DayCount);
        }

        [TestMethod]
        public void Custom_Reversed_IsSwapped()
        {
            var window = TimeWindow.Custom(new DateTime(2026, 5, 1), new DateTime(2026, 4, 1));

            Assert.AreEqual(new DateTime(2026, 4, 1), window.From);
            Assert.AreEqual(new DateTime(2026, 5, 1), window.To);
        }

        [TestMethod]
        public void Custom_BothBlank_IsAll()
        {
            Assert.AreEqual(TimeWindow.All, TimeWindow.Custom(null, null));
            Assert.IsTrue(TimeWindow.Custom(null, null).IsUnbounded);
        }

        [TestMethod]
        public void Custom_FutureTo_IsNotClamped()
        {
            var window = TimeWindow.Custom(Today, Today.AddDays(3));
            var range = window.Resolve(Today, null);

            Assert.AreEqual(Today.AddDays(3), range.End);
        }

        [TestMethod]
        public void ResolveBounds_ShapesPerKind()
        {
            Assert.IsTrue(TimeWindow.All.ResolveBounds(Today).IsUnbounded);

            var preset = TimeWindow.FromPreset(TimelineRange.OneMonth).ResolveBounds(Today);
            Assert.AreEqual(new DateTime(2026, 8, 29), preset.Start);
            Assert.AreEqual(Today, preset.End);

            var toOnly = TimeWindow.Custom(null, new DateTime(2026, 6, 30)).ResolveBounds(Today);
            Assert.IsNull(toOnly.Start);
            Assert.AreEqual(new DateTime(2026, 6, 30), toOnly.End);
            Assert.IsTrue(toOnly.Contains(new DateTime(1999, 1, 1)));
            Assert.IsFalse(toOnly.Contains(new DateTime(2026, 7, 1)));
        }

        [TestMethod]
        public void Key_RoundTripsEveryForm()
        {
            var windows = new[]
            {
                TimeWindow.FromPreset(TimelineRange.SevenDays),
                TimeWindow.FromPreset(TimelineRange.OneYear),
                TimeWindow.All,
                TimeWindow.Custom(new DateTime(2024, 1, 1), new DateTime(2024, 6, 30)),
                TimeWindow.Custom(new DateTime(2024, 1, 1), null),
                TimeWindow.Custom(null, new DateTime(2024, 6, 30))
            };

            foreach (var window in windows)
            {
                Assert.IsTrue(TimeWindow.TryParse(window.ToKey(), out var parsed), window.ToKey());
                Assert.AreEqual(window, parsed, window.ToKey());
                Assert.AreEqual(window.GetHashCode(), parsed.GetHashCode());
            }

            Assert.AreEqual("OneYear", TimeWindow.FromPreset(TimelineRange.OneYear).ToKey());
            Assert.AreEqual("Custom:2024-01-01..", TimeWindow.Custom(new DateTime(2024, 1, 1), null).ToKey());
            Assert.AreEqual("Custom:..2024-06-30", TimeWindow.Custom(null, new DateTime(2024, 6, 30)).ToKey());
        }

        [TestMethod]
        public void TryParse_AcceptsLegacyForms_RejectsJunk()
        {
            Assert.IsTrue(TimeWindow.TryParse("oneyear", out var lower));
            Assert.AreEqual(TimelineRange.OneYear, lower.Preset);

            Assert.IsTrue(TimeWindow.TryParse("4", out var ordinal));
            Assert.AreEqual(TimelineRange.OneYear, ordinal.Preset);

            Assert.IsTrue(TimeWindow.TryParse("Custom:..", out var blank));
            Assert.AreEqual(TimeWindow.All, blank);

            Assert.IsFalse(TimeWindow.TryParse(null, out _));
            Assert.IsFalse(TimeWindow.TryParse("  ", out _));
            Assert.IsFalse(TimeWindow.TryParse("TwoYears", out _));
            Assert.IsFalse(TimeWindow.TryParse("99", out _));
            Assert.IsFalse(TimeWindow.TryParse("Custom:2024-13-01..", out _));
            Assert.IsFalse(TimeWindow.TryParse("Custom:2024-01-01", out _));
        }

        [TestMethod]
        public void Equality_IsByValue()
        {
            var a = TimeWindow.Custom(new DateTime(2024, 1, 1), null);
            var b = TimeWindow.Custom(new DateTime(2024, 1, 1, 10, 0, 0), null);

            Assert.IsTrue(a == b);
            Assert.IsTrue(a.Equals((object)b));
            Assert.IsFalse(a != b);
            Assert.AreNotEqual(a, TimeWindow.FromPreset(TimelineRange.SevenDays));
            Assert.IsFalse(a == null);
            Assert.IsTrue((TimeWindow)null == null);
        }

        private sealed class Holder
        {
            [JsonConverter(typeof(TimeWindowJsonConverter))]
            public TimeWindow Window { get; set; } = TimeWindow.FromPreset(TimelineRange.ThreeMonths);
        }

        [TestMethod]
        public void JsonConverter_ReadsIntegerStringObjectAndNull()
        {
            Assert.AreEqual(TimelineRange.OneYear, JsonConvert.DeserializeObject<Holder>("{\"Window\":4}").Window.Preset);
            Assert.AreEqual(TimelineRange.SevenDays, JsonConvert.DeserializeObject<Holder>("{\"Window\":\"SevenDays\"}").Window.Preset);
            Assert.AreEqual(
                TimeWindow.Custom(new DateTime(2024, 1, 1), new DateTime(2024, 6, 30)),
                JsonConvert.DeserializeObject<Holder>("{\"Window\":\"Custom:2024-01-01..2024-06-30\"}").Window);
            Assert.AreEqual(
                TimelineRange.All,
                JsonConvert.DeserializeObject<Holder>("{\"Window\":{\"Preset\":\"All\"}}").Window.Preset);
            Assert.AreEqual(
                TimeWindow.Custom(new DateTime(2024, 1, 1), null),
                JsonConvert.DeserializeObject<Holder>("{\"Window\":{\"From\":\"2024-01-01\",\"To\":null}}").Window);
            Assert.AreEqual(TimelineRange.ThreeMonths, JsonConvert.DeserializeObject<Holder>("{\"Window\":null}").Window.Preset);
            Assert.AreEqual(TimelineRange.ThreeMonths, JsonConvert.DeserializeObject<Holder>("{\"Window\":\"junk\"}").Window.Preset);
            Assert.AreEqual(TimelineRange.ThreeMonths, JsonConvert.DeserializeObject<Holder>("{\"Window\":99}").Window.Preset);
        }

        [TestMethod]
        public void JsonConverter_WritesCanonicalString()
        {
            var json = JsonConvert.SerializeObject(new Holder { Window = TimeWindow.Custom(null, new DateTime(2024, 6, 30)) });

            Assert.AreEqual("{\"Window\":\"Custom:..2024-06-30\"}", json);
        }
    }
}
