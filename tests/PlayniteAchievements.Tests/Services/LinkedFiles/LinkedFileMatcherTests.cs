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

        private static LinkedFileSchema Schema(params (string column, LinkedFileField field)[] mappings)
        {
            return new LinkedFileSchema
            {
                Columns = mappings.Select(mapping => new LinkedFileColumnMapping { Column = mapping.column, Field = mapping.field }).ToList()
            };
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
        public void ApiNameThenDisplayName_FallsThroughInOrder()
        {
            var achievements = new[] { Achievement("a", "Alpha"), Achievement("b", "Beta") };
            var table = Table(new[] { "id", "name" }, new[] { "a", "wrong" }, new[] { "zzz", "beta" });

            var result = LinkedFileMatcher.Match(
                table,
                Schema(("id", LinkedFileField.ApiName), ("name", LinkedFileField.DisplayName)),
                achievements);

            Assert.AreEqual(2, result.Matches.Count);
            Assert.AreEqual(LinkedFileMatchKey.ApiName, result.Matches[0].MatchedBy);
            Assert.AreEqual(LinkedFileMatchKey.DisplayName, result.Matches[1].MatchedBy);
        }

        [TestMethod]
        public void RowOrder_AppliesOnlyWithoutANameColumnAndWhenRowsFit()
        {
            var achievements = new[] { Achievement("a", "A"), Achievement("b", "B"), Achievement("c", "C") };
            var schema = Schema(("done", LinkedFileField.Unlocked));

            var fits = LinkedFileMatcher.Match(Table(new[] { "done" }, new[] { "1" }, new[] { "0" }), schema, achievements);
            Assert.AreEqual(2, fits.Matches.Count);
            Assert.AreEqual("a", fits.Matches[0].Achievement.ApiName);
            Assert.AreEqual("b", fits.Matches[1].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.RowOrder, fits.Matches[1].MatchedBy);
            Assert.AreEqual("c", fits.UnmatchedAchievements.Single().ApiName);

            var tooMany = LinkedFileMatcher.Match(Table(new[] { "done" }, new[] { "1" }, new[] { "0" }, new[] { "1" }, new[] { "1" }), schema, achievements);
            Assert.AreEqual(0, tooMany.Matches.Count);
            Assert.AreEqual(4, tooMany.UnmatchedRows.Count);
            Assert.IsTrue(tooMany.Diagnostics.Single().Contains("4 rows"));

            var afterApiName = LinkedFileMatcher.Match(
                Table(new[] { "id" }, new[] { "b" }, new[] { "nope" }),
                new LinkedFileSchema
                {
                    Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = "id", Field = LinkedFileField.ApiName } }
                },
                achievements);
            Assert.AreEqual(1, afterApiName.Matches.Count);
            CollectionAssert.AreEqual(new[] { 1 }, afterApiName.UnmatchedRows.ToArray(), "With an API name column, rows never pair by order.");
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
            var pairings = new Dictionary<string, string> { ["row:1"] = "c", ["row:2"] = "gone" };

            var result = LinkedFileMatcher.Match(table, Schema(("done", LinkedFileField.Unlocked)), achievements, pairings);

            Assert.AreEqual(2, result.Matches.Count);
            Assert.AreEqual("c", result.Matches[0].Achievement.ApiName);
            Assert.AreEqual("b", result.Matches[1].Achievement.ApiName);
            Assert.AreEqual(LinkedFileMatchKey.RowOrder, result.Matches[1].MatchedBy);
            CollectionAssert.AreEqual(new[] { 2 }, result.UnmatchedRows.ToArray(), "Row 3's achievement was paired by hand to row 1.");
            Assert.IsTrue(result.Diagnostics.Any(line => line.Contains("'gone'")));
        }

        [TestMethod]
        public void RowKey_UsesTheApiNameThenTheNameThenThePosition()
        {
            var table = Table(new[] { "id", "name" }, new[] { " x ", "Ex" }, new[] { "", "Why" }, new[] { "", "" });
            var both = Schema(("id", LinkedFileField.ApiName), ("name", LinkedFileField.DisplayName));

            Assert.AreEqual("id:x", LinkedFileMatcher.RowKey(table, both, 0));
            Assert.AreEqual("name:Why", LinkedFileMatcher.RowKey(table, both, 1));
            Assert.AreEqual("row:3", LinkedFileMatcher.RowKey(table, both, 2));
            Assert.AreEqual("row:1", LinkedFileMatcher.RowKey(table, Schema(), 0));
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
