using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileTableReadersTests
    {
        private static LinkedFileReadResult ParseText(string text, LinkedFileSchema schema, Encoding encoding = null)
        {
            return LinkedFileTableReaders.Parse((encoding ?? new UTF8Encoding(false)).GetBytes(text), schema);
        }

        /// <summary>The row's own cells, without the position column every table ends with.</summary>
        private static string[] Row(LinkedFileTable table, int index)
        {
            var cells = table.Rows[index];
            Assert.AreEqual(
                (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                cells[cells.Count - 1],
                "The position column holds the row's 1-based number.");
            return cells.Take(cells.Count - 1).ToArray();
        }

        // ---------------------------------------------------------------- delimited

        [TestMethod]
        public void Delimited_ReadsQuotedCellsBomAndCrlf_AndSkipsBlankRows()
        {
            var text = "\uFEFFid,name,done\r\n\"a,1\",\"He said \"\"hi\"\"\",true\r\n\r\nb,Second,false\r\n";

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Delimited });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "id", "name", "done", "$row" }, result.Table.Columns.ToArray());
            Assert.AreEqual(2, result.Table.Rows.Count);
            CollectionAssert.AreEqual(new[] { "a,1", "He said \"hi\"", "true" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "b", "Second", "false" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Delimited_HonoursSeparatorLineExplicitPipeAndDetectedSemicolon()
        {
            var declared = ParseText("sep=;\nid;done\nx;1\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited });
            CollectionAssert.AreEqual(new[] { "id", "done", "$row" }, declared.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "x", "1" }, Row(declared.Table, 0));

            var pipe = ParseText("id|done\na|1\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited, Delimiter = "|" });
            CollectionAssert.AreEqual(new[] { "a", "1" }, Row(pipe.Table, 0));

            var detectedPipe = ParseText("id|done\na|1\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited });
            CollectionAssert.AreEqual(new[] { "a", "1" }, Row(detectedPipe.Table, 0));

            var semicolon = ParseText("id;done\na;1\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited });
            CollectionAssert.AreEqual(new[] { "a", "1" }, Row(semicolon.Table, 0));
        }

        [TestMethod]
        public void Delimited_WithoutHeader_NamesColumnsByPosition()
        {
            var result = ParseText("a,1\nb,2\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited, HasHeader = false });

            CollectionAssert.AreEqual(new[] { "1", "2", "$row" }, result.Table.Columns.ToArray());
            Assert.AreEqual(2, result.Table.Rows.Count);
            Assert.AreEqual(0, result.Table.IndexOfColumn("1"));
            Assert.AreEqual(1, result.Table.IndexOfColumn("2"));
            Assert.AreEqual(2, result.Table.IndexOfColumn("3"), "The position column is the third.");
            Assert.AreEqual(-1, result.Table.IndexOfColumn("4"));
        }

        [TestMethod]
        public void Delimited_SpaceSeparator_ReadsRunsOfSpacesAsOne()
        {
            var result = ParseText("45 1792954109\r\n1   1792954482\r\n  7 1793462635  \r\n\r\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited, Delimiter = " ", HasHeader = false });

            CollectionAssert.AreEqual(new[] { "1", "2", "$row" }, result.Table.Columns.ToArray());
            Assert.AreEqual(3, result.Table.Rows.Count);
            CollectionAssert.AreEqual(new[] { "45", "1792954109" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "1", "1792954482" }, Row(result.Table, 1));
            CollectionAssert.AreEqual(new[] { "7", "1793462635" }, Row(result.Table, 2));
        }

        [TestMethod]
        public void Delimited_RaggedRowsArePaddedAndExtraCellsGetPositionalColumns()
        {
            var result = ParseText("id,done\na\nb,1,extra\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited });

            CollectionAssert.AreEqual(new[] { "id", "done", "3", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "a", "", "" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "b", "1", "extra" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Delimited_RowWithAnotherSeparator_IsWarnedAbout()
        {
            var result = ParseText("id,done\nx;1\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited });

            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, result.Warnings.Count);
            StringAssert.Contains(result.Warnings[0], "Row 2");
        }

        [TestMethod]
        public void Delimited_DuplicateAndBlankHeadersAreMadeUnique()
        {
            var result = ParseText("id,,id\n1,2,3\n", new LinkedFileSchema { Format = LinkedFileFormat.Delimited });

            CollectionAssert.AreEqual(new[] { "id", "2", "id (2)", "$row" }, result.Table.Columns.ToArray());
            Assert.AreEqual(2, result.Table.IndexOfColumn("ID (2)"));
        }

        // ---------------------------------------------------------------- json

        [TestMethod]
        public void Json_ArrayOfObjects_FlattensNestedObjectsIntoDottedColumns()
        {
            var text = "[{\"id\":\"a\",\"stats\":{\"earned\":true,\"time\":\"2026-01-02T03:04:05Z\"},\"tags\":[\"x\"]},{\"id\":\"b\",\"stats\":{\"earned\":false}}]";

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Json });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "id", "stats.earned", "stats.time", "tags", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "a", "true", "2026-01-02T03:04:05Z", "[\"x\"]" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "b", "false", "", "" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Json_ObjectOfObjects_ExposesTheKeyColumnFirst()
        {
            var result = ParseText("{\"a\":{\"done\":true},\"b\":{\"done\":false,\"t\":1}}", new LinkedFileSchema { Format = LinkedFileFormat.Json });

            CollectionAssert.AreEqual(new[] { "$key", "done", "t", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "a", "true", "" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "b", "false", "1" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Json_ObjectOfScalarsAndScalarArrays_UseValueColumns()
        {
            var scalars = ParseText("{\"NEON\":\"true\",\"X\":null}", new LinkedFileSchema { Format = LinkedFileFormat.Json });
            CollectionAssert.AreEqual(new[] { "$key", "$value", "$row" }, scalars.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "NEON", "true" }, Row(scalars.Table, 0));
            CollectionAssert.AreEqual(new[] { "X", "" }, Row(scalars.Table, 1));

            var list = ParseText("[7, \"eight\", 9.5]", new LinkedFileSchema { Format = LinkedFileFormat.Json });
            CollectionAssert.AreEqual(new[] { "$value", "$row" }, list.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "7" }, Row(list.Table, 0));
            CollectionAssert.AreEqual(new[] { "eight" }, Row(list.Table, 1));
            CollectionAssert.AreEqual(new[] { "9.5" }, Row(list.Table, 2));
        }

        [TestMethod]
        public void Json_RootPathAndWildcards_SelectTheRows()
        {
            var nested = ParseText("{\"data\":{\"Achievements\":[{\"id\":1}]}}", new LinkedFileSchema { Format = LinkedFileFormat.Json, RootPath = "data.achievements" });
            Assert.IsTrue(nested.Success, nested.Message);
            CollectionAssert.AreEqual(new[] { "1" }, Row(nested.Table, 0));

            var wildcard = ParseText(
                "{\"players\":[{\"name\":\"p1\",\"ach\":[{\"id\":1},{\"id\":2}]},{\"name\":\"p2\",\"ach\":[{\"id\":3}]}]}",
                new LinkedFileSchema { Format = LinkedFileFormat.Json, RootPath = "players[*].ach[*]" });
            Assert.AreEqual(3, wildcard.Table.Rows.Count);
            CollectionAssert.AreEqual(new[] { "3" }, Row(wildcard.Table, 2));

            var missing = ParseText("{\"a\":1}", new LinkedFileSchema { Format = LinkedFileFormat.Json, RootPath = "nope" });
            Assert.AreEqual(LinkedFileReadStatus.SchemaInvalid, missing.Status);
        }

        [TestMethod]
        public void Json_ListsEveryListAndObjectAsARowsPath()
        {
            const string text = "{\"Count\": 0, \"LevelData\": {\"a1\": {}, \"a2\": {}}, \"achievements\": [], \"odd.name\": [], \"nested\": {\"list\": [1]}}";
            var schema = new LinkedFileSchema { Format = LinkedFileFormat.Json };

            var result = ParseText(text, schema);

            CollectionAssert.AreEqual(
                new[] { "LevelData", "achievements", "nested", "nested.list" },
                result.RowPaths.ToArray());

            var missing = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Json, RootPath = "nope" });
            Assert.AreEqual(LinkedFileReadStatus.SchemaInvalid, missing.Status);
            Assert.AreEqual(4, missing.RowPaths.Count, "A wrong path still offers the file's paths.");
        }

        [TestMethod]
        public void Json_EmptyListStillNamesItsValueColumn()
        {
            var result = ParseText("{\"achievements\": []}", new LinkedFileSchema { Format = LinkedFileFormat.Json, RootPath = "achievements" });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "$value", "$row" }, result.Table.Columns.ToArray());
            Assert.AreEqual(0, result.Table.Rows.Count);
        }

        [TestMethod]
        public void Json_InvalidText_ReadFailed()
        {
            var result = ParseText("{\"a\":", new LinkedFileSchema { Format = LinkedFileFormat.Json });

            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, result.Status);
            Assert.IsNotNull(result.Message);
        }

        // ---------------------------------------------------------------- toml

        [TestMethod]
        public void Toml_TablesOfTables_ReadLikeAnObjectOfObjects()
        {
            var text = string.Join("\n", new[]
            {
                "# Unlock state",
                "title = \"Progress\"",
                "",
                "[unlocked.101]",
                "filetime = 133000000000000000",
                "",
                "[unlocked.102]",
                "filetime = 133_000_000_000_000_001 # trailing comment",
                "name = 'Second \"one\"'",
                "big = 123456789012345678901234567890",
            });

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Toml, RootPath = "unlocked" });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "$key", "filetime", "name", "big", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "101", "133000000000000000", "", "" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "102", "133000000000000001", "Second \"one\"", "123456789012345678901234567890" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Toml_SingleTopLevelTable_IsOfferedAsTheAchievementList()
        {
            var text = string.Join("\r\n", new[]
            {
                "# Achievement unlock state - managed by the runtime",
                "[unlocked.1]",
                "filetime = 134394237123357469",
                "",
                "[unlocked.45]",
                "filetime = 134394238634904551",
            });

            var whole = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Toml });
            CollectionAssert.AreEqual(new[] { "unlocked" }, whole.RowPaths.ToArray());

            var listed = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Toml, RootPath = "unlocked" });
            Assert.IsTrue(listed.Success, listed.Message);
            CollectionAssert.AreEqual(new[] { "$key", "filetime", "$row" }, listed.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "1", "134394237123357469" }, Row(listed.Table, 0));
            CollectionAssert.AreEqual(new[] { "45", "134394238634904551" }, Row(listed.Table, 1));

            var json = ParseText("{\"achievements\": {\"a\": {\"done\": 1}, \"b\": {\"done\": 0}}}", new LinkedFileSchema { Format = LinkedFileFormat.Json });
            CollectionAssert.AreEqual(new[] { "achievements" }, json.RowPaths.ToArray());

            var table = ParseText("{\"a\": {\"done\": 1}, \"b\": {\"done\": 0}}", new LinkedFileSchema { Format = LinkedFileFormat.Json });
            Assert.AreEqual(0, table.RowPaths.Count, "A root that is itself a table offers no list.");
        }

        [TestMethod]
        public void Toml_ArraysOfTablesInlineTablesAndDates_ReadAsRows()
        {
            var text = string.Join("\n", new[]
            {
                "[[runs]]",
                "id = 1",
                "tags = [\"a\", \"b\",]",
                "done = true",
                "",
                "[[runs]]",
                "id = 2",
                "point = { x = 1, y = 2.5 }",
                "when = 2026-01-02T03:04:05Z",
                "day = 2026-01-02 03:04:05",
                "escaped = \"tab\\there \\u00e9\"",
            });

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Toml, RootPath = "runs" });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "id", "tags", "done", "point.x", "point.y", "when", "day", "escaped", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "1", "[\"a\",\"b\"]", "true", "", "", "", "", "" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "2", "", "", "1", "2.5", "2026-01-02T03:04:05Z", "2026-01-02 03:04:05", "tab\there \u00e9" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Toml_InvalidSyntax_ReadFailedWithLine()
        {
            var result = ParseText("ok = 1\nbroken = \n", new LinkedFileSchema { Format = LinkedFileFormat.Toml });

            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, result.Status);
            StringAssert.Contains(result.Message, "line 2");
        }

        // ---------------------------------------------------------------- xlsx

        private static byte[] BuildWorkbook(bool truncate = false)
        {
            const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            using (var stream = new MemoryStream())
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    void Add(string name, string content)
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)))
                        {
                            writer.Write(content);
                        }
                    }

                    Add("xl/workbook.xml",
                        "<workbook xmlns=\"" + main + "\" xmlns:r=\"" + rel + "\"><sheets>" +
                        "<sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/>" +
                        "<sheet name=\"Other\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
                    Add("xl/_rels/workbook.xml.rels",
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId2\" Type=\"x\" Target=\"worksheets/sheet2.xml\"/>" +
                        "<Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                    Add("xl/sharedStrings.xml",
                        "<sst xmlns=\"" + main + "\"><si><t>id</t></si><si><r><t>do</t></r><r><t>ne</t></r></si></sst>");
                    Add("xl/styles.xml",
                        "<styleSheet xmlns=\"" + main + "\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm\"/></numFmts>" +
                        "<cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>");
                    Add("xl/worksheets/sheet1.xml",
                        "<worksheet xmlns=\"" + main + "\"><sheetData>" +
                        "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"inlineStr\"><is><t>when</t></is></c></row>" +
                        "<row r=\"2\"><c r=\"A2\" t=\"str\"><v>ach_1</v></c><c r=\"B2\" t=\"b\"><v>1</v></c><c r=\"C2\" s=\"1\"><v>45658.5</v></c></row>" +
                        "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>ach_2</t></is></c><c r=\"C3\"><v>12</v></c></row>" +
                        "<row r=\"4\"/>" +
                        "</sheetData></worksheet>");
                    Add("xl/worksheets/sheet2.xml",
                        "<worksheet xmlns=\"" + main + "\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>other</t></is></c></row></sheetData></worksheet>");
                }

                var bytes = stream.ToArray();
                return truncate ? bytes.Take(bytes.Length / 2).ToArray() : bytes;
            }
        }

        [TestMethod]
        public void Xlsx_ReadsTheFirstSheetWithSharedInlineBooleanNumberAndDateCells()
        {
            var result = LinkedFileTableReaders.Parse(BuildWorkbook(), new LinkedFileSchema { Format = LinkedFileFormat.Xlsx });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "Data", "Other" }, result.SheetNames.ToArray());
            CollectionAssert.AreEqual(new[] { "id", "done", "when", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "ach_1", "true", "2025-01-01T12:00:00" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "ach_2", "", "12" }, Row(result.Table, 1));
            Assert.AreEqual(2, result.Table.Rows.Count);
        }

        [TestMethod]
        public void Xlsx_SheetByNameMissingSheetAndTruncatedFile()
        {
            var other = LinkedFileTableReaders.Parse(BuildWorkbook(), new LinkedFileSchema { Format = LinkedFileFormat.Xlsx, SheetName = "other", HasHeader = false });
            Assert.IsTrue(other.Success, other.Message);
            CollectionAssert.AreEqual(new[] { "other" }, Row(other.Table, 0));

            var missing = LinkedFileTableReaders.Parse(BuildWorkbook(), new LinkedFileSchema { Format = LinkedFileFormat.Xlsx, SheetName = "Nope" });
            Assert.AreEqual(LinkedFileReadStatus.SchemaInvalid, missing.Status);
            CollectionAssert.AreEqual(new[] { "Data", "Other" }, missing.SheetNames.ToArray());

            var truncated = LinkedFileTableReaders.Parse(BuildWorkbook(truncate: true), new LinkedFileSchema { Format = LinkedFileFormat.Xlsx });
            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, truncated.Status);
        }

        [TestMethod]
        public void Xlsx_ColumnReferences()
        {
            Assert.AreEqual(0, XlsxTableReader.ColumnIndex("A1"));
            Assert.AreEqual(2, XlsxTableReader.ColumnIndex("C7"));
            Assert.AreEqual(26, XlsxTableReader.ColumnIndex("AA3"));
            Assert.AreEqual(-1, XlsxTableReader.ColumnIndex("7"));
            Assert.AreEqual(-1, XlsxTableReader.ColumnIndex(null));
        }

        // ---------------------------------------------------------------- xml

        [TestMethod]
        public void Xml_ElementsWithAttributesChildrenAndText()
        {
            var text = "<achievements online=\"1\"><achievement id=\"3\" achieved=\"1\"/><achievement id=\"4\" achieved=\"0\">note</achievement>" +
                       "<achievement id=\"5\"><name>Five</name><name>Alt</name></achievement></achievements>";

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Xml, RowElementPath = "achievements.achievement" });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "@id", "@achieved", "$text", "name", "name (2)", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "3", "1", "", "", "" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "4", "0", "note", "", "" }, Row(result.Table, 1));
            CollectionAssert.AreEqual(new[] { "5", "", "", "Five", "Alt" }, Row(result.Table, 2));
        }

        [TestMethod]
        public void Xml_ScalarListAndRelativeOrBlankPaths()
        {
            var text = "<SaveGame><player><achievements><int>3</int><int>12</int></achievements></player></SaveGame>";

            var relative = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Xml, RowElementPath = "player/achievements/int" });
            Assert.IsTrue(relative.Success, relative.Message);
            CollectionAssert.AreEqual(new[] { "$value", "$row" }, relative.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "3" }, Row(relative.Table, 0));
            CollectionAssert.AreEqual(new[] { "12" }, Row(relative.Table, 1));

            var blank = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Xml });
            Assert.AreEqual(1, blank.Table.Rows.Count, "A blank path takes the root's children.");
            CollectionAssert.AreEqual(new[] { "achievements", "$row" }, blank.Table.Columns.ToArray());

            var none = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Xml, RowElementPath = "nothing.here" });
            Assert.IsTrue(none.Success);
            Assert.AreEqual(0, none.Table.Rows.Count);
        }

        [TestMethod]
        public void Xml_Utf16WithBomDtdAndTruncation()
        {
            var utf16 = ParseText("<r><a x=\"1\"/></r>", new LinkedFileSchema { Format = LinkedFileFormat.Xml }, Encoding.Unicode);
            Assert.IsTrue(utf16.Success, "The XML reader detects UTF-16 from the first bytes even without a BOM.");
            CollectionAssert.AreEqual(new[] { "1" }, Row(utf16.Table, 0));

            var withBom = LinkedFileTableReaders.Parse(
                Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<r><a x=\"1\"/></r>")).ToArray(),
                new LinkedFileSchema { Format = LinkedFileFormat.Xml });
            Assert.IsTrue(withBom.Success, withBom.Message);
            CollectionAssert.AreEqual(new[] { "1" }, Row(withBom.Table, 0));

            var dtd = ParseText("<!DOCTYPE r [<!ENTITY x \"y\">]><r><a>&x;</a></r>", new LinkedFileSchema { Format = LinkedFileFormat.Xml });
            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, dtd.Status);

            var truncated = ParseText("<r><a x=\"1\"/>", new LinkedFileSchema { Format = LinkedFileFormat.Xml });
            Assert.AreEqual(LinkedFileReadStatus.ReadFailed, truncated.Status);
        }

        // ---------------------------------------------------------------- ini

        [TestMethod]
        public void Ini_SectionsBecomeRowsWithTheUnionOfKeys()
        {
            var text = "; comment\n[ach_1]\nAchieved=1\nUnlockTime = 1700000000\n\n[ach_2]\nAchieved=\"0\"\n# another comment\nAchieved='0'\n";

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Ini });

            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "$section", "Achieved", "UnlockTime", "$row" }, result.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "ach_1", "1", "1700000000" }, Row(result.Table, 0));
            CollectionAssert.AreEqual(new[] { "ach_2", "0", "" }, Row(result.Table, 1));
        }

        [TestMethod]
        public void Ini_KeysBecomeRowsWithAColumnPerSection()
        {
            const string text = "; achievements earned\n[notice]\nshown=1\n[unlocked]\nach_one=2026-11-06 21:57:15\nach_two=2026-11-06 21:57:42\n[progress]\nach_three=21\nach_one=5\n";

            var result = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Ini });
            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(new[] { "$key", "notice", "unlocked", "progress", "$row" }, result.Table.Columns.ToArray());
            Assert.AreEqual(4, result.Table.Rows.Count);
            CollectionAssert.AreEqual(new[] { "ach_one", "", "2026-11-06 21:57:15", "5" }, Row(result.Table, 1), "A key in two sections is one row.");
            CollectionAssert.AreEqual(new[] { "ach_three", "", "", "21" }, Row(result.Table, 3));

            var schema = LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Format = LinkedFileFormat.Ini,
                Columns = new List<LinkedFileColumnMapping>
                {
                    new LinkedFileColumnMapping { Column = "$key", Field = LinkedFileField.ApiName },
                    new LinkedFileColumnMapping { Column = "unlocked", Field = LinkedFileField.UnlockTime },
                    new LinkedFileColumnMapping { Column = "progress", Field = LinkedFileField.ProgressNum }
                }
            });
            var plan = LinkedFileApplier.Build(
                result.Table, schema, null,
                new List<AchievementDetail> { new AchievementDetail { ApiName = "ach_one" }, new AchievementDetail { ApiName = "ach_two" }, new AchievementDetail { ApiName = "ach_three" } },
                new ManualAchievementLink { SourceKey = "Steam", SourceGameId = "1" },
                null,
                new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.AreEqual(0, plan.Report.ParseErrors, string.Join(" ", plan.Report.Messages));
            Assert.AreEqual(2, plan.Report.Unlocked);
            Assert.AreEqual(
                new DateTime(2026, 11, 6, 21, 57, 15, DateTimeKind.Local).ToUniversalTime(),
                plan.ManualUnlocks["ach_one"]);
            Assert.IsFalse(plan.ManualUnlocks.ContainsKey("ach_three"), "A key without an unlock time stays locked.");
            Assert.AreEqual(21, plan.ManualProgress.Single(update => update.ApiName == "ach_three").ProgressNum);

            var single = ParseText("[unlocked]\na=1\nb=2\n", new LinkedFileSchema { Format = LinkedFileFormat.Ini });
            CollectionAssert.AreEqual(new[] { "$key", "unlocked", "$row" }, single.Table.Columns.ToArray());
            Assert.AreEqual(2, single.Table.Rows.Count, "One section lists its keys as rows.");
        }

        [TestMethod]
        public void Ini_MixedLayoutsPickTheirShape()
        {
            Func<string, string[]> columns = text =>
                ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Ini }).Table.Columns.ToArray();

            CollectionAssert.AreEqual(
                new[] { "$key", "unlocked", "progress", "$row" },
                columns("[unlocked]\na=1\nb=0\nc=0\nd=1\ne=0\n[progress]\na=10\nb=3\nc=0\nd=10\ne=7\n"),
                "Every achievement in both sections is still one row per achievement.");
            CollectionAssert.AreEqual(
                new[] { "$section", "Achieved", "UnlockTime", "Progress", "Max", "$row" },
                columns("[a]\nAchieved=1\nUnlockTime=1\nProgress=1\nMax=1\n[b]\nAchieved=0\nUnlockTime=0\nProgress=0\nMax=1\n[c]\nAchieved=0\nUnlockTime=0\nProgress=0\nMax=1\n"),
                "A few achievements with several fields each are still one row per section.");
            CollectionAssert.AreEqual(
                new[] { "$section", "version", "Achieved", "$row" },
                columns("version=2\n[a]\nAchieved=1\n[b]\nAchieved=0\n[c]\nAchieved=1\n"),
                "Loose keys before the sections do not change the shape.");
        }

        [TestMethod]
        public void Ini_MappingKeepsItsShapeWhenOneSectionIsLeft()
        {
            const string text = "[ach_one]\nAchieved=1\nUnlockTime=1700000000\n";

            var guessed = ParseText(text, new LinkedFileSchema { Format = LinkedFileFormat.Ini });
            CollectionAssert.AreEqual(new[] { "$key", "ach_one", "$row" }, guessed.Table.Columns.ToArray(), "One section alone reads as a list of keys.");

            var mapped = ParseText(text, new LinkedFileSchema
            {
                Format = LinkedFileFormat.Ini,
                Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = "$section", Field = LinkedFileField.ApiName } }
            });
            CollectionAssert.AreEqual(new[] { "$section", "Achieved", "UnlockTime", "$row" }, mapped.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "ach_one", "1", "1700000000" }, Row(mapped.Table, 0));
        }

        [TestMethod]
        public void Ini_WithoutSections_KeysAreRows()
        {
            var flat = ParseText("# earned: id=time\n101 = 1700000000\n102=1700000500\n", new LinkedFileSchema { Format = LinkedFileFormat.Ini });
            CollectionAssert.AreEqual(new[] { "$key", "$value", "$row" }, flat.Table.Columns.ToArray());
            CollectionAssert.AreEqual(new[] { "101", "1700000000" }, Row(flat.Table, 0));
            CollectionAssert.AreEqual(new[] { "102", "1700000500" }, Row(flat.Table, 1));
        }

        // ---------------------------------------------------------------- files

        [TestMethod]
        public void Read_MissingFileBlankPathAndEmptyFile()
        {
            Assert.AreEqual(LinkedFileReadStatus.NoFilePath, LinkedFileTableReaders.Read(" ", new LinkedFileSchema()).Status);
            Assert.AreEqual(LinkedFileReadStatus.SchemaInvalid, LinkedFileTableReaders.Read("x.csv", null).Status);

            var folder = CreateTempFolder();
            try
            {
                Assert.AreEqual(LinkedFileReadStatus.FileMissing, LinkedFileTableReaders.Read(Path.Combine(folder, "nope.csv"), new LinkedFileSchema()).Status);

                var empty = Path.Combine(folder, "empty.csv");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                Assert.AreEqual(LinkedFileReadStatus.FileUnstable, LinkedFileTableReaders.Read(empty, new LinkedFileSchema()).Status);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestMethod]
        public void Read_FileHeldOpenForWriting_IsReadable()
        {
            var folder = CreateTempFolder();
            try
            {
                var path = Path.Combine(folder, "progress.csv");
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    var bytes = Encoding.UTF8.GetBytes("id,done\na,1\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();

                    var result = LinkedFileTableReaders.Read(path, new LinkedFileSchema { Format = LinkedFileFormat.Delimited });

                    Assert.IsTrue(result.Success, result.Message);
                    CollectionAssert.AreEqual(new[] { "a", "1" }, Row(result.Table, 0));
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestMethod]
        public void Read_Windows1252Bytes_DecodeThroughTheFallback()
        {
            var folder = CreateTempFolder();
            try
            {
                var path = Path.Combine(folder, "progress.csv");
                File.WriteAllBytes(path, Encoding.GetEncoding(1252).GetBytes("id,name\na,Caf\u00e9\n"));

                var result = LinkedFileTableReaders.Read(path, new LinkedFileSchema { Format = LinkedFileFormat.Delimited });

                Assert.IsTrue(result.Success, result.Message);
                Assert.AreEqual("Caf\u00e9", result.Table.Rows[0][1]);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestMethod]
        public void DetectFormat_ByExtension()
        {
            Assert.AreEqual(LinkedFileFormat.Delimited, LinkedFileTableReaders.DetectFormat(@"C:\x\progress.TSV"));
            Assert.AreEqual(LinkedFileFormat.Json, LinkedFileTableReaders.DetectFormat("a.json"));
            Assert.AreEqual(LinkedFileFormat.Toml, LinkedFileTableReaders.DetectFormat("a.toml"));
            Assert.AreEqual(LinkedFileFormat.Xlsx, LinkedFileTableReaders.DetectFormat("a.xlsx"));
            Assert.AreEqual(LinkedFileFormat.Xml, LinkedFileTableReaders.DetectFormat("a.xml"));
            Assert.AreEqual(LinkedFileFormat.Ini, LinkedFileTableReaders.DetectFormat("a.ini"));
            Assert.AreEqual(LinkedFileFormat.Binary, LinkedFileTableReaders.DetectFormat("a.bin"));
            Assert.IsNull(LinkedFileTableReaders.DetectFormat("a.exe"));
            Assert.IsNull(LinkedFileTableReaders.DetectFormat(null));
        }

        private static string CreateTempFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
