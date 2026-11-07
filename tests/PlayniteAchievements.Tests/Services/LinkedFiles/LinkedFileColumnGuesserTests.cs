using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.LinkedFiles;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileColumnGuesserTests
    {
        private static Dictionary<string, LinkedFileField> Guess(string[] columns, params string[][] rows)
        {
            return LinkedFileColumnGuesser.Guess(LinkedFileTable.Build(columns, rows))
                .ToDictionary(mapping => mapping.Column, mapping => mapping.Field);
        }

        [TestMethod]
        public void TheAchievementCsvHeaderMapsEveryColumn()
        {
            var columns = CustomAchievementCsvFormat.Header.Split(',');
            var guessed = Guess(
                columns,
                new[] { "c1", "First", "Do it", "10", "gold", "false", "12.5%", "Story", "3", "5", "true", "2025-01-02 03:04:05", "https://x/u.png", "https://x/l.png" },
                new[] { "c2", "Second", "Do more", "20", "bronze", "true", "Rare", "Side", "", "", "false", "", "", "" });

            CollectionAssert.AreEquivalent(
                new[]
                {
                    LinkedFileField.ApiName, LinkedFileField.DisplayName, LinkedFileField.Description, LinkedFileField.Points,
                    LinkedFileField.TrophyType, LinkedFileField.Hidden, LinkedFileField.Rarity, LinkedFileField.Category,
                    LinkedFileField.ProgressNum, LinkedFileField.ProgressDenom, LinkedFileField.Unlocked, LinkedFileField.UnlockTime,
                    LinkedFileField.UnlockedIconPath, LinkedFileField.LockedIconPath
                },
                columns.Select(column => guessed[column]).ToArray());
        }

        [TestMethod]
        public void SaveFileHeadersResolveThroughSpellingAndAffixes()
        {
            var guessed = Guess(
                new[] { "achievementName", "achieved", "unlock_time", "icon_gray", "max_progress", "is_hidden", "UnlockTimeUtc" },
                new[] { "ACH_1", "1", "1700000000", "gray.png", "10", "0", "" });

            Assert.AreEqual(LinkedFileField.DisplayName, guessed["achievementName"]);
            Assert.AreEqual(LinkedFileField.Unlocked, guessed["achieved"]);
            Assert.AreEqual(LinkedFileField.UnlockTime, guessed["unlock_time"]);
            Assert.AreEqual(LinkedFileField.LockedIconPath, guessed["icon_gray"]);
            Assert.AreEqual(LinkedFileField.ProgressDenom, guessed["max_progress"]);
            Assert.AreEqual(LinkedFileField.Hidden, guessed["is_hidden"]);
            Assert.IsFalse(guessed.ContainsKey("UnlockTimeUtc"), "A field is claimed by one column only.");
        }

        [TestMethod]
        public void KeyColumnNamesTheRowUnlessARealHeaderDoes()
        {
            Assert.AreEqual(LinkedFileField.ApiName, Guess(new[] { "$key", "$value" }, new[] { "ACH_1", "1700000000" })["$key"]);

            var named = Guess(new[] { "$section", "id" }, new[] { "s1", "ACH_1" });
            Assert.AreEqual(LinkedFileField.ApiName, named["id"]);
            Assert.IsFalse(named.ContainsKey("$section"));
        }

        [TestMethod]
        public void AnAmbiguousHeaderTakesTheFieldItsCellsFit()
        {
            var words = Guess(new[] { "id", "done" }, new[] { "a", "yes" }, new[] { "b", "no" });
            var counts = Guess(new[] { "id", "done" }, new[] { "a", "3" }, new[] { "b", "7" });

            Assert.AreEqual(LinkedFileField.Unlocked, words["done"]);
            Assert.AreEqual(LinkedFileField.Unlocked, counts["done"], "Numbers read as booleans, so the first fitting field wins.");

            var value = Guess(new[] { "id", "value" }, new[] { "a", "-3" });
            Assert.IsFalse(value.ContainsKey("value"), "Negative numbers fit neither progress nor points.");
        }

        [TestMethod]
        public void AHeaderWhoseCellsDoNotFitFallsToSniffing()
        {
            var guessed = Guess(new[] { "id", "status" }, new[] { "a", "2024-05-01T10:00:00Z" }, new[] { "b", "2024-06-01T10:00:00Z" });

            Assert.AreEqual(LinkedFileField.UnlockTime, guessed["status"]);
        }

        [TestMethod]
        public void HeaderlessColumnsAreSniffedByTheirCells()
        {
            var guessed = Guess(
                new[] { "1", "2", "3", "4", "5" },
                new[] { "ACH_ONE", "true", "1700000000", "https://x/a.png", "Gold" },
                new[] { "ACH_TWO", "false", "0", "https://x/b.png", "Silver" });

            Assert.AreEqual(LinkedFileField.ApiName, guessed["1"]);
            Assert.AreEqual(LinkedFileField.Unlocked, guessed["2"]);
            Assert.AreEqual(LinkedFileField.UnlockTime, guessed["3"]);
            Assert.AreEqual(LinkedFileField.UnlockedIconPath, guessed["4"]);
            Assert.AreEqual(LinkedFileField.TrophyType, guessed["5"]);
        }

        [TestMethod]
        public void SniffingLeavesCountersAndProseAlone()
        {
            var guessed = Guess(
                new[] { "1", "2", "3", "4" },
                new[] { "First One", "1", "5", "same" },
                new[] { "Second One", "1", "12", "same" });

            Assert.AreEqual(LinkedFileField.DisplayName, guessed["1"]);
            Assert.IsFalse(guessed.ContainsKey("2"), "A column of only 1 is a counter as often as a state.");
            Assert.IsFalse(guessed.ContainsKey("3"), "Small numbers are not times.");
            Assert.IsFalse(guessed.ContainsKey("4"), "Repeated text names no achievement.");
        }

        [TestMethod]
        public void CsvImportRecognizesOnlyItsOwnHeaders()
        {
            Assert.AreEqual(LinkedFileField.ApiName, LinkedFileColumnGuesser.ResolveCsvHeader("ID"));
            Assert.AreEqual(LinkedFileField.ProgressDenom, LinkedFileColumnGuesser.ResolveCsvHeader("Progress Total"));
            Assert.AreEqual(LinkedFileField.Ignore, LinkedFileColumnGuesser.ResolveCsvHeader("icon_gray"));
        }
    }
}
