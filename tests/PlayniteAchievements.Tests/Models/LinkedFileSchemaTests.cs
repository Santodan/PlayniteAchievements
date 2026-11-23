using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class LinkedFileSchemaTests
    {
        [TestMethod]
        public void Normalize_DropsIgnoredBlankAndDuplicateMappings()
        {
            var schema = new LinkedFileSchema
            {
                Columns = new List<LinkedFileColumnMapping>
                {
                    new LinkedFileColumnMapping { Column = " id ", Field = LinkedFileField.ApiName },
                    new LinkedFileColumnMapping { Column = "notes", Field = LinkedFileField.Ignore },
                    new LinkedFileColumnMapping { Column = "  ", Field = LinkedFileField.Unlocked },
                    null,
                    new LinkedFileColumnMapping { Column = "ID", Field = LinkedFileField.DisplayName },
                    new LinkedFileColumnMapping { Column = "name", Field = LinkedFileField.DisplayName },
                    new LinkedFileColumnMapping { Column = "title", Field = LinkedFileField.DisplayName }
                }
            };

            var normalized = LinkedFileSchema.Normalize(schema);

            CollectionAssert.AreEqual(
                new[] { "id:ApiName", "name:DisplayName" },
                normalized.Columns.Select(mapping => mapping.Column + ":" + mapping.Field).ToArray());
        }

        [TestMethod]
        public void Normalize_EmptyMappingsBecomeNull()
        {
            var normalized = LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Columns = new List<LinkedFileColumnMapping>
                {
                    new LinkedFileColumnMapping { Column = "x", Field = LinkedFileField.Ignore }
                }
            });

            Assert.IsNull(normalized.Columns);
        }

        [TestMethod]
        public void Normalize_DelimiterAliases()
        {
            Assert.AreEqual("\t", LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = "tab" }).Delimiter);
            Assert.AreEqual("\t", LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = "\\t" }).Delimiter);
            Assert.AreEqual("\t", LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = "\t" }).Delimiter);
            Assert.AreEqual(";", LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = " ;; " }).Delimiter);
            Assert.AreEqual(" ", LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = " " }).Delimiter);
            Assert.AreEqual(" ", LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = "Space" }).Delimiter);
            Assert.IsNull(LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = "" }).Delimiter);
            Assert.IsNull(LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = "  " }).Delimiter);
            Assert.IsNull(LinkedFileSchema.Normalize(new LinkedFileSchema { Delimiter = null }).Delimiter);
            Assert.AreEqual('|', new LinkedFileSchema { Delimiter = "|" }.DelimiterChar);
            Assert.IsNull(new LinkedFileSchema().DelimiterChar);
        }

        [TestMethod]
        public void Normalize_TrueValuesAreLowercasedAndDeduped()
        {
            var normalized = LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                UnlockedTrueValues = new List<string> { " Yes", "yes", "", null, "1" }
            });

            CollectionAssert.AreEqual(new[] { "yes", "1" }, normalized.UnlockedTrueValues.ToArray());
            Assert.IsNull(LinkedFileSchema.Normalize(new LinkedFileSchema { UnlockedTrueValues = new List<string> { " " } }).UnlockedTrueValues);
            CollectionAssert.AreEqual(LinkedFileSchema.DefaultTrueValues, new LinkedFileSchema().EffectiveTrueValues.ToArray());
        }

        [TestMethod]
        public void Presence_FollowsTheMappedColumns()
        {
            LinkedFileSchema With(params LinkedFileField[] fields) => LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Columns = fields.Select((field, i) => new LinkedFileColumnMapping { Column = "c" + i, Field = field }).ToList()
            });

            Assert.IsTrue(With(LinkedFileField.ApiName).RowPresenceMeansUnlocked, "A list of ids means each listed one is unlocked.");
            Assert.IsFalse(With(LinkedFileField.ApiName, LinkedFileField.Unlocked).RowPresenceMeansUnlocked);
            Assert.IsFalse(With(LinkedFileField.ApiName, LinkedFileField.UnlockTime).RowPresenceMeansUnlocked);
            Assert.IsFalse(With(LinkedFileField.ApiName, LinkedFileField.ProgressNum).RowPresenceMeansUnlocked);
            Assert.IsFalse(With(LinkedFileField.DisplayName, LinkedFileField.Description).RowPresenceMeansUnlocked, "A list of definitions says nothing about unlocks.");
            Assert.IsFalse(new LinkedFileSchema().RowPresenceMeansUnlocked, "Without a match column no row says which achievement it is.");
            Assert.IsTrue(new LinkedFileSchema { MatchColumn = "id" }.RowPresenceMeansUnlocked, "A list of ids with nothing else mapped.");

            Assert.IsTrue(With(LinkedFileField.Order).RowPresenceMeansUnlocked, "A list of positions means each listed one is unlocked.");
            Assert.IsFalse(With(LinkedFileField.Order, LinkedFileField.Unlocked).RowPresenceMeansUnlocked);
        }

        [TestMethod]
        public void Normalize_GivesAnOlderMappingTheMatchItWasReadWith()
        {
            LinkedFileSchema With(params LinkedFileField[] fields) => LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Columns = fields.Select((field, i) => new LinkedFileColumnMapping { Column = "c" + i, Field = field }).ToList()
            });

            var byApiName = With(LinkedFileField.DisplayName, LinkedFileField.ApiName);
            Assert.AreEqual("c1", byApiName.MatchColumn, "API Name comes before Name.");
            Assert.AreEqual(LinkedFileMatchBy.ApiName, byApiName.MatchBy);
            Assert.AreEqual(2, byApiName.Columns.Count, "Both stay mapped to feed new achievements.");

            var byOrder = With(LinkedFileField.DisplayName, LinkedFileField.Order);
            Assert.AreEqual("c1", byOrder.MatchColumn, "Order comes before Name.");
            Assert.AreEqual(LinkedFileMatchBy.PositionFromOne, byOrder.MatchBy);
            Assert.IsFalse(byOrder.Columns.Any(mapping => mapping.Field == LinkedFileField.Order), "Order feeds no field of its own.");

            var byName = With(LinkedFileField.DisplayName, LinkedFileField.Unlocked);
            Assert.AreEqual("c0", byName.MatchColumn);
            Assert.AreEqual(LinkedFileMatchBy.Name, byName.MatchBy);

            var byPosition = With(LinkedFileField.Unlocked, LinkedFileField.UnlockTime);
            Assert.AreEqual(LinkedFileSchema.PositionColumn, byPosition.MatchColumn);
            Assert.AreEqual(LinkedFileMatchBy.PositionFromOne, byPosition.MatchBy);

            var chosen = LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Columns = byApiName.Columns,
                MatchColumn = " c0 ",
                MatchBy = LinkedFileMatchBy.Name
            });
            Assert.AreEqual("c0", chosen.MatchColumn, "A chosen match is kept.");
            Assert.AreEqual(LinkedFileMatchBy.Name, chosen.MatchBy);

            Assert.IsNull(
                LinkedFileSchema.Normalize(new LinkedFileSchema { Columns = byPosition.Columns }, upgrade: false).MatchColumn,
                "The Link File window's own mapping is not upgraded.");
            Assert.IsNull(LinkedFileSchema.Normalize(new LinkedFileSchema()).MatchColumn, "A mapping with no columns reads nothing.");
        }

        [TestMethod]
        public void Normalize_CustomTimeFormatWithoutAPatternFallsBackToAuto()
        {
            Assert.AreEqual(
                LinkedFileTimeFormat.Auto,
                LinkedFileSchema.Normalize(new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.Custom, CustomTimeFormat = " " }).TimeFormat);

            var kept = LinkedFileSchema.Normalize(new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.Custom, CustomTimeFormat = " dd.MM.yyyy " });
            Assert.AreEqual(LinkedFileTimeFormat.Custom, kept.TimeFormat);
            Assert.AreEqual("dd.MM.yyyy", kept.CustomTimeFormat);
        }

        [TestMethod]
        public void Normalize_TrimsNamesAndPathsAndCapsTheName()
        {
            var normalized = LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Name = " " + new string('n', 80) + " ",
                SheetName = " Sheet 2 ",
                RootPath = " data.items ",
                RowElementPath = "",
                AssumeLocalTime = false
            });

            Assert.AreEqual(LinkedFileSchema.MaxNameLength, normalized.Name.Length);
            Assert.AreEqual("Sheet 2", normalized.SheetName);
            Assert.AreEqual("data.items", normalized.RootPath);
            Assert.IsNull(normalized.RowElementPath);
            Assert.IsFalse(normalized.AssumeLocalTime);
            Assert.IsNull(LinkedFileSchema.Normalize(new LinkedFileSchema { Name = "  " }).Name);
            Assert.IsNull(LinkedFileSchema.Normalize(null));
        }

        [TestMethod]
        public void Clone_IsDeep()
        {
            var schema = new LinkedFileSchema
            {
                Name = "a",
                Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = "id", Field = LinkedFileField.ApiName } },
                UnlockedTrueValues = new List<string> { "x" }
            };

            var clone = schema.Clone();
            clone.Columns[0].Column = "changed";
            clone.UnlockedTrueValues.Add("y");

            Assert.AreEqual("id", schema.Columns[0].Column);
            Assert.AreEqual(1, schema.UnlockedTrueValues.Count);

            var link = new LinkedFileLink { FilePath = "p", Schema = schema };
            var linkClone = link.Clone();
            Assert.AreNotSame(link.Schema, linkClone.Schema);
            Assert.AreEqual("p", linkClone.FilePath);
            Assert.IsTrue(link.HasFilePath);
            Assert.IsFalse(new LinkedFileLink { FilePath = " " }.HasFilePath);
        }

        [TestMethod]
        public void TryGetColumn_FindsTheMappedColumn()
        {
            var schema = new LinkedFileSchema
            {
                Columns = new List<LinkedFileColumnMapping>
                {
                    new LinkedFileColumnMapping { Column = "id", Field = LinkedFileField.ApiName },
                    new LinkedFileColumnMapping { Column = "when", Field = LinkedFileField.UnlockTime }
                }
            };

            Assert.IsTrue(schema.TryGetColumn(LinkedFileField.UnlockTime, out var column));
            Assert.AreEqual("when", column);
            Assert.IsFalse(schema.TryGetColumn(LinkedFileField.Unlocked, out _));
            Assert.IsTrue(schema.HasColumn(LinkedFileField.ApiName));
            Assert.IsFalse(new LinkedFileSchema().HasColumn(LinkedFileField.ApiName));
        }
    }
}
