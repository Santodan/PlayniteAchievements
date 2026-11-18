using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileMatcherTests
    {
        private static LinkedFileTable Table(string[] columns, params string[][] rows)
        {
            return LinkedFileTable.Build(columns, rows);
        }

        private static AchievementDetail Achievement(string apiName, string displayName, bool isCustom = false)
        {
            return new AchievementDetail { ApiName = apiName, DisplayName = displayName, IsCustom = isCustom };
        }

        /// <summary>A mapping as a stored one reads: <see cref="LinkedFileSchema.Normalize"/> gives it its match column.</summary>
        private static LinkedFileSchema Schema(params (string column, LinkedFileField field)[] mappings)
        {
            return LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Columns = mappings.Select(mapping => new LinkedFileColumnMapping { Column = mapping.column, Field = mapping.field }).ToList()
            });
        }

        private static LinkedFileSchema MatchBy(string column, LinkedFileMatchBy by)
        {
            return new LinkedFileSchema { MatchColumn = column, MatchBy = by };
        }

        [TestMethod]
        public void ApiName_MatchesExactlyAndToleratesTheCustomPrefix()
        {
            var achievements = new[]
            {
                Achievement("custom:c1", "Custom One", isCustom: true),
                Achievement("ach_two", "Two")
            };
            var table = Table(new[] { "id" }, new[] { "c1" }, new[] { "ACH_TWO" }, new[] { "custom:c1" });

            var result = LinkedFileMatcher.Match(table, Schema(("id", LinkedFileField.ApiName)), achievements);

            Assert.AreEqual(2, result.Matches.Count);
            Assert.AreEqual("custom:c1", result.Matches[0].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.ApiName, result.Matches[0].MatchedBy);
            Assert.AreEqual("ach_two", result.Matches[1].Achievement.ApiName);
            CollectionAssert.AreEqual(new[] { 2 }, result.UnmatchedRows.ToArray(), "The achievement is already taken by the bare id row.");
            Assert.AreEqual(0, result.UnmatchedAchievements.Count);
        }

        [TestMethod]
        public void ApiName_DuplicateRowKeysLeaveBothRowsUnmatched()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B") };
            var table = Table(new[] { "id" }, new[] { "a" }, new[] { "A" }, new[] { "b" });

            var result = LinkedFileMatcher.Match(table, Schema(("id", LinkedFileField.ApiName)), achievements);

            Assert.AreEqual(1, result.Matches.Count);
            Assert.AreEqual("b", result.Matches[0].Achievement.ApiName);
            CollectionAssert.AreEqual(new[] { 0, 1 }, result.UnmatchedRows.ToArray());
            Assert.AreEqual(2, result.Diagnostics.Count);
            Assert.AreEqual("a", result.UnmatchedAchievements.Single().ApiName);
        }

        [TestMethod]
        public void DisplayName_MatchesNormalizedTextAndSkipsAmbiguousNames()
        {
            var achievements = new[]
            {
                Achievement("one", "Café Noir!"),
                Achievement("two", "Twin"),
                Achievement("three", "twin")
            };
            var table = Table(new[] { "name" }, new[] { "cafe noir" }, new[] { "TWIN" });

            var result = LinkedFileMatcher.Match(table, Schema(("name", LinkedFileField.DisplayName)), achievements);

            Assert.AreEqual(1, result.Matches.Count);
            Assert.AreEqual("one", result.Matches[0].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.DisplayName, result.Matches[0].MatchedBy);
            CollectionAssert.AreEqual(new[] { 1 }, result.UnmatchedRows.ToArray());
            Assert.IsTrue(result.Diagnostics.Any(message => message.Contains("more than one achievement")));
        }

        [TestMethod]
        public void OnlyTheMatchColumnPairs()
        {
            var achievements = new[] { Achievement("a", "Alpha"), Achievement("b", "Beta") };
            var table = Table(new[] { "id", "name" }, new[] { "a", "wrong" }, new[] { "zzz", "beta" });

            var result = LinkedFileMatcher.Match(
                table,
                Schema(("id", LinkedFileField.ApiName), ("name", LinkedFileField.DisplayName)),
                achievements);

            Assert.AreEqual("a", result.Matches.Single().Achievement.ApiName);
            CollectionAssert.AreEqual(new[] { 1 }, result.UnmatchedRows.ToArray(), "The name column feeds the name only.");

            var none = LinkedFileMatcher.Match(table, new LinkedFileSchema(), achievements);
            Assert.AreEqual(0, none.Matches.Count, "Without a match column only hand pairings pair.");
        }

        [TestMethod]
        public void PositionColumn_PairsEachRowWithTheAchievementAtItsPlace()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };
            var schema = Schema(("done", LinkedFileField.Unlocked));
            Assert.AreEqual(LinkedFileSchema.PositionColumn, schema.MatchColumn, "An older mapping naming no achievement matched by row order.");

            var fits = LinkedFileMatcher.Match(Table(new[] { "done" }, new[] { "1" }, new[] { "0" }), schema, achievements);
            Assert.AreEqual(2, fits.Matches.Count);
            Assert.AreEqual("a", fits.Matches[0].Achievement.ApiName);
            Assert.AreEqual("b", fits.Matches[1].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.Order, fits.Matches[1].MatchedBy);
            Assert.AreEqual("c", fits.UnmatchedAchievements.Single().ApiName);

            // A file with more rows than the game has achievements matches the rows it can.
            var tooMany = LinkedFileMatcher.Match(Table(new[] { "done" }, new[] { "1" }, new[] { "0" }, new[] { "1" }, new[] { "1" }), schema, achievements);
            Assert.AreEqual(3, tooMany.Matches.Count);
            CollectionAssert.AreEqual(new[] { 3 }, tooMany.UnmatchedRows.ToArray());
            Assert.IsTrue(tooMany.Diagnostics.Single().Contains("outside the game's 3 achievements"));

            var withApiName = LinkedFileMatcher.Match(
                Table(new[] { "id" }, new[] { "b" }, new[] { "nope" }),
                Schema(("id", LinkedFileField.ApiName)),
                achievements);
            Assert.AreEqual(1, withApiName.Matches.Count);
            CollectionAssert.AreEqual(new[] { 1 }, withApiName.UnmatchedRows.ToArray(), "An API name column identifies the rows, so position is not used.");
        }

        [TestMethod]
        public void Position_PicksTheAchievementAtThePositionWhateverTheRowOrder()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };

            var oneBased = LinkedFileMatcher.Match(
                Table(new[] { "n" }, new[] { "3" }, new[] { "1" }), MatchBy("n", LinkedFileMatchBy.PositionFromOne), achievements);
            Assert.AreEqual(2, oneBased.Matches.Count);
            Assert.AreEqual("c", oneBased.Matches[0].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.Order, oneBased.Matches[0].MatchedBy);
            Assert.AreEqual("a", oneBased.Matches[1].Achievement.ApiName);
            Assert.AreEqual("b", oneBased.UnmatchedAchievements.Single().ApiName);

            var zeroBased = LinkedFileMatcher.Match(
                Table(new[] { "n" }, new[] { "2" }, new[] { "1" }), MatchBy("n", LinkedFileMatchBy.PositionFromZero), achievements);
            Assert.AreEqual("c", zeroBased.Matches[0].Achievement.ApiName);
            Assert.AreEqual("b", zeroBased.Matches[1].Achievement.ApiName);
        }

        [TestMethod]
        public void Order_LeavesDuplicateOutOfRangeAndUnreadablePositionsUnmatched()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };
            var table = Table(new[] { "n" }, new[] { "1" }, new[] { "1" }, new[] { "4" }, new[] { "x" }, new[] { "" }, new[] { "2" });

            var result = LinkedFileMatcher.Match(table, Schema(("n", LinkedFileField.Order)), achievements);

            Assert.AreEqual("n", Schema(("n", LinkedFileField.Order)).MatchColumn, "An older Order column becomes the match column.");
            Assert.IsNull(Schema(("n", LinkedFileField.Order)).Columns, "Order feeds no field of its own.");
            Assert.AreEqual("b", result.Matches.Single().Achievement.ApiName);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, result.UnmatchedRows.ToArray());
            Assert.AreEqual(2, result.Diagnostics.Count(message => message.Contains("more than one row")));
            Assert.AreEqual(1, result.Diagnostics.Count(message => message.Contains("outside the game's 3 achievements")));
            Assert.AreEqual(1, result.Diagnostics.Count(message => message.Contains("is not a position")));
        }

        [TestMethod]
        public void Order_KeysHandPairingsByThePosition()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };
            var schema = Schema(("n", LinkedFileField.Order));
            var table = Table(new[] { "n" }, new[] { "2" }, new[] { "1" });
            Assert.AreEqual("order:2", LinkedFileMatcher.RowKey(table, schema, 0));

            var result = LinkedFileMatcher.Match(table, schema, achievements, new Dictionary<string, string> { ["order:2"] = "c" });

            Assert.AreEqual("c", result.Matches[0].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.Paired, result.Matches[0].MatchedBy);
            Assert.AreEqual("a", result.Matches[1].Achievement.ApiName);
        }

        [TestMethod]
        public void Pairings_ComeFirstAndANoneRowIsSkipped()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };
            var table = Table(new[] { "id" }, new[] { "x" }, new[] { "a" }, new[] { "y" }, new[] { "c" });
            var pairings = new Dictionary<string, string> { ["id:x"] = "C", ["id:y"] = "" };

            var result = LinkedFileMatcher.Match(table, Schema(("id", LinkedFileField.ApiName)), achievements, pairings);

            Assert.AreEqual(2, result.Matches.Count);
            Assert.AreEqual("c", result.Matches[0].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.Paired, result.Matches[0].MatchedBy);
            Assert.AreEqual("a", result.Matches[1].Achievement.ApiName);
            CollectionAssert.AreEqual(new[] { 2 }, result.SkippedRows.ToArray());
            CollectionAssert.AreEqual(new[] { 3 }, result.UnmatchedRows.ToArray(), "The row's own id is taken by the hand pairing.");
            Assert.AreEqual("b", result.UnmatchedAchievements.Single().ApiName);
        }

        [TestMethod]
        public void Pairings_KeepRowOrderForTheOtherRowsAndIgnoreMissingAchievements()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };
            var table = Table(new[] { "done" }, new[] { "1" }, new[] { "0" }, new[] { "1" });
            // A pairing stored before the position column existed still finds its row.
            var pairings = new Dictionary<string, string> { ["row:1"] = "c", ["row:2"] = "gone" };

            var result = LinkedFileMatcher.Match(table, Schema(("done", LinkedFileField.Unlocked)), achievements, pairings);

            Assert.AreEqual(2, result.Matches.Count);
            Assert.AreEqual("c", result.Matches[0].Achievement.ApiName);
            Assert.AreEqual("b", result.Matches[1].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.Order, result.Matches[1].MatchedBy);
            CollectionAssert.AreEqual(new[] { 2 }, result.UnmatchedRows.ToArray(), "Row 3's achievement was paired by hand to row 1.");
            Assert.IsTrue(result.Diagnostics.Any(line => line.Contains("'gone'")));
        }

        [TestMethod]
        public void RowKey_UsesTheMatchCellElseThePosition()
        {
            var table = Table(new[] { "id", "name" }, new[] { " x ", "Ex" }, new[] { "", "Why" }, new[] { "", "" });

            Assert.AreEqual("id:x", LinkedFileMatcher.RowKey(table, MatchBy("id", LinkedFileMatchBy.ApiName), 0));
            Assert.AreEqual("row:2", LinkedFileMatcher.RowKey(table, MatchBy("id", LinkedFileMatchBy.ApiName), 1), "A blank match cell keys by position.");
            Assert.AreEqual("name:Why", LinkedFileMatcher.RowKey(table, MatchBy("name", LinkedFileMatchBy.Name), 1));
            Assert.AreEqual("row:1", LinkedFileMatcher.RowKey(table, new LinkedFileSchema(), 0));
            Assert.AreEqual(
                "row:2",
                LinkedFileMatcher.RowKey(table, MatchBy(LinkedFileSchema.PositionColumn, LinkedFileMatchBy.PositionFromOne), 1),
                "The position column keeps the key a row has always had.");
        }

        [TestMethod]
        public void MissingColumnsAndBlankCellsMatchNothing()
        {
            var achievements = new[] { Achievement("a", "A") };
            var table = Table(new[] { "other" }, new[] { "" }, new[] { "a" });

            var result = LinkedFileMatcher.Match(table, Schema(("id", LinkedFileField.ApiName)), achievements);

            Assert.AreEqual(0, result.Matches.Count);
            Assert.AreEqual(2, result.UnmatchedRows.Count);
            Assert.AreEqual(1, result.UnmatchedAchievements.Count);
        }
    }
}
