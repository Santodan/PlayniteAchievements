using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Globalization;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers which achievement fields the merged editor lets a user change, and how their text is
    /// parsed. The editability rules are the guard rails on a page that can now edit provider data,
    /// so they are pinned here rather than left implicit in the grid row.
    /// </summary>
    [TestClass]
    public class AchievementEditorFieldRulesTests
    {
        [TestMethod]
        public void UnlockStatus_StaysProviderOwnedOnAProviderRow()
        {
            // Editing it would move unlocked counts and completion, and look like a real unlock to
            // the in-game monitor.
            Assert.IsFalse(AchievementEditorFieldRules.CanEditUnlockStatus(
                isCustomRow: false,
                isManuallyTrackedGame: false));
        }

        [TestMethod]
        public void UnlockStatus_IsEditableOnAnAuthoredAchievement()
        {
            Assert.IsTrue(AchievementEditorFieldRules.CanEditUnlockStatus(
                isCustomRow: true,
                isManuallyTrackedGame: false));
        }

        [TestMethod]
        public void UnlockStatus_IsEditableOnEveryRowOfAManuallyTrackedGame()
        {
            // Recording unlocks by hand is the whole of manual tracking, so the provider-owned rule
            // does not apply: there is no provider behind those rows to disagree with.
            Assert.IsTrue(AchievementEditorFieldRules.CanEditUnlockStatus(
                isCustomRow: false,
                isManuallyTrackedGame: true));
        }

        [TestMethod]
        public void Rarity_IsEditableOnlyForCustomAchievements()
        {
            Assert.IsFalse(AchievementEditorFieldRules.CanEditRarity(isCustomRow: false));
            Assert.IsTrue(AchievementEditorFieldRules.CanEditRarity(isCustomRow: true));
        }

        [TestMethod]
        public void Rarity_BlankOnAnAuthoredRowReadsAsCommon()
        {
            // The stored definition already defaulted blank to Common on save; the row now shows
            // the same value so the field never looks optional.
            Assert.AreEqual("Common", AchievementEditorFieldRules.NormalizeAuthoredRarity(null, isBulkRow: false));
            Assert.AreEqual("Common", AchievementEditorFieldRules.NormalizeAuthoredRarity("   ", isBulkRow: false));
        }

        [TestMethod]
        public void Rarity_BlankOnTheBulkProxyStaysBlank()
        {
            // Blank on the proxy means the selected rows disagree, not Common.
            Assert.IsNull(AchievementEditorFieldRules.NormalizeAuthoredRarity(null, isBulkRow: true));
            Assert.IsNull(AchievementEditorFieldRules.NormalizeAuthoredRarity(string.Empty, isBulkRow: true));
        }

        [TestMethod]
        public void Rarity_SuppliedValueIsTrimmedAndKept()
        {
            Assert.AreEqual("Rare", AchievementEditorFieldRules.NormalizeAuthoredRarity(" Rare ", isBulkRow: false));
            Assert.AreEqual("Rare", AchievementEditorFieldRules.NormalizeAuthoredRarity("Rare", isBulkRow: true));
        }

        [TestMethod]
        public void UnlockTime_IsEditableOnlyWhileUnlocked()
        {
            Assert.IsFalse(AchievementEditorFieldRules.CanEditUnlockTime(unlocked: false));
            Assert.IsTrue(AchievementEditorFieldRules.CanEditUnlockTime(unlocked: true));
        }

        [TestMethod]
        public void TryParsePoints_BlankClearsTheOverride()
        {
            Assert.IsTrue(AchievementEditorFieldRules.TryParsePoints(null, out var fromNull));
            Assert.IsNull(fromNull);

            Assert.IsTrue(AchievementEditorFieldRules.TryParsePoints("   ", out var fromBlank));
            Assert.IsNull(fromBlank);
        }

        [TestMethod]
        public void TryParsePoints_AcceptsNonNegativeIntegers()
        {
            Assert.IsTrue(AchievementEditorFieldRules.TryParsePoints("0", out var zero));
            Assert.AreEqual(0, zero);

            Assert.IsTrue(AchievementEditorFieldRules.TryParsePoints(" 75 ", out var padded));
            Assert.AreEqual(75, padded);
        }

        [TestMethod]
        public void TryParsePoints_RejectsNegativeAndNonNumeric()
        {
            Assert.IsFalse(AchievementEditorFieldRules.TryParsePoints("-5", out _));
            Assert.IsFalse(AchievementEditorFieldRules.TryParsePoints("abc", out _));
            Assert.IsFalse(AchievementEditorFieldRules.TryParsePoints("1.5", out _));
        }

        [TestMethod]
        public void TryParseUnlockTime_BlankClearsTheOverride()
        {
            Assert.IsTrue(AchievementEditorFieldRules.TryParseUnlockTimeUtc(null, out var fromNull));
            Assert.IsNull(fromNull);

            Assert.IsTrue(AchievementEditorFieldRules.TryParseUnlockTimeUtc("  ", out var fromBlank));
            Assert.IsNull(fromBlank);
        }

        [TestMethod]
        public void TryParseUnlockTime_ReturnsUtc()
        {
            Assert.IsTrue(AchievementEditorFieldRules.TryParseUnlockTimeUtc("2026-03-01T12:00:00Z", out var parsed));
            Assert.IsTrue(parsed.HasValue);
            Assert.AreEqual(DateTimeKind.Utc, parsed.Value.Kind);
            Assert.AreEqual(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), parsed.Value);
        }

        [TestMethod]
        public void TryParseUnlockTime_TreatsOffsetlessTextAsLocal()
        {
            // The field displays local time, so text without an offset must be read back as local
            // or a round trip would shift the timestamp by the machine's offset.
            var local = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Local);
            var text = local.ToString("g", CultureInfo.CurrentCulture);

            Assert.IsTrue(AchievementEditorFieldRules.TryParseUnlockTimeUtc(text, out var parsed));
            Assert.AreEqual(DateTimeKind.Utc, parsed.Value.Kind);
            Assert.AreEqual(local.ToUniversalTime().ToString("g", CultureInfo.CurrentCulture),
                parsed.Value.ToLocalTime().ToUniversalTime().ToString("g", CultureInfo.CurrentCulture));
        }

        [TestMethod]
        public void TryParseUnlockTime_RejectsUnparsableText()
        {
            Assert.IsFalse(AchievementEditorFieldRules.TryParseUnlockTimeUtc("not a date", out _));
        }

        [TestMethod]
        public void UnlockTime_RoundTripsThroughTheEditingFormat()
        {
            var stored = new DateTime(2026, 3, 1, 15, 30, 0, DateTimeKind.Utc);

            var text = AchievementEditorFieldRules.FormatUnlockTimeForEditing(stored);
            Assert.IsTrue(AchievementEditorFieldRules.TryParseUnlockTimeUtc(text, out var parsed));

            // The editing format carries minutes, so the round trip is exact to the minute.
            Assert.AreEqual(stored.Year, parsed.Value.Year);
            Assert.AreEqual(stored.Month, parsed.Value.Month);
            Assert.AreEqual(stored.Day, parsed.Value.Day);
            Assert.AreEqual(stored.Hour, parsed.Value.Hour);
            Assert.AreEqual(stored.Minute, parsed.Value.Minute);
        }

        [TestMethod]
        public void FormatUnlockTimeForEditing_BlankWhenNotUnlocked()
        {
            Assert.IsNull(AchievementEditorFieldRules.FormatUnlockTimeForEditing(null));
        }

        [TestMethod]
        public void PrefersTwentyFourHourClock_FollowsTheCulturesOwnTimePattern()
        {
            // en-US writes 1:30 PM, de-DE and ja-JP write 13:30.
            Assert.IsFalse(AchievementEditorFieldRules.PrefersTwentyFourHourClock(new CultureInfo("en-US")));
            Assert.IsTrue(AchievementEditorFieldRules.PrefersTwentyFourHourClock(new CultureInfo("de-DE")));
            Assert.IsTrue(AchievementEditorFieldRules.PrefersTwentyFourHourClock(new CultureInfo("ja-JP")));
        }

        [TestMethod]
        public void PrefersTwentyFourHourClock_IgnoresAQuotedLiteralHour()
        {
            // A pattern whose separator is a quoted 'h' must not read as the 12-hour specifier.
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.DateTimeFormat.ShortTimePattern = "HH'h'mm";

            Assert.IsTrue(AchievementEditorFieldRules.PrefersTwentyFourHourClock(culture));
        }

        [TestMethod]
        public void PrefersTwentyFourHourClock_UnknownCulture_FallsBackToTwelveHour()
        {
            Assert.IsFalse(AchievementEditorFieldRules.PrefersTwentyFourHourClock(null));
        }
    }
}
