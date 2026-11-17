using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// An Excel workbook sheet as a table, read straight from the package's XML parts: the
    /// schema's sheet or the first one, cached cell values only (formulas are not evaluated),
    /// dates recognized by their number format and written as ISO text, the first row as the
    /// header when the schema says so.
    /// </summary>
    internal static class XlsxTableReader
    {
        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null
        };

        private static readonly HashSet<int> BuiltInDateFormats = new HashSet<int> { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };

        public static LinkedFileReadResult Read(byte[] bytes, LinkedFileSchema schema)
        {
            try
            {
                using (var stream = new MemoryStream(bytes ?? Array.Empty<byte>()))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var sheets = ReadSheets(archive);
                    var sheetNames = sheets.Select(sheet => sheet.Name).ToList();
                    if (sheets.Count == 0)
                    {
                        return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The workbook has no sheets.", sheetNames);
                    }

                    var wanted = (schema.SheetName ?? string.Empty).Trim();
                    var sheet = wanted.Length == 0
                        ? sheets[0]
                        : sheets.FirstOrDefault(candidate => string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase));
                    if (sheet == null)
                    {
                        return LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "The workbook has no sheet named '" + wanted + "'.", sheetNames);
                    }

                    var entry = archive.GetEntry(sheet.Path);
                    if (entry == null)
                    {
                        return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The sheet part '" + sheet.Path + "' is missing.", sheetNames);
                    }

                    var sharedStrings = ReadSharedStrings(archive);
                    var dateStyles = ReadDateStyles(archive);
                    var rows = ReadSheetRows(entry, sharedStrings, dateStyles);
                    var nonBlank = rows.Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToList();
                    if (nonBlank.Count == 0)
                    {
                        return LinkedFileReadResult.Ok(LinkedFileTable.Empty, null, sheetNames);
                    }

                    IEnumerable<string> columns;
                    if (schema.HasHeader)
                    {
                        columns = nonBlank[0];
                        nonBlank = nonBlank.Skip(1).ToList();
                    }
                    else
                    {
                        var width = nonBlank.Max(row => row.Count);
                        columns = Enumerable.Range(1, width).Select(index => index.ToString(CultureInfo.InvariantCulture));
                    }

                    return LinkedFileReadResult.Ok(LinkedFileTable.Build(columns, nonBlank), null, sheetNames);
                }
            }
            catch (InvalidDataException ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The workbook could not be opened: " + ex.Message);
            }
            catch (XmlException ex)
            {
                return LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "The workbook could not be read: " + ex.Message);
            }
        }

        private sealed class SheetInfo
        {
            public string Name { get; set; }

            public string Path { get; set; }
        }

        private static List<SheetInfo> ReadSheets(ZipArchive archive)
        {
            var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (relsEntry != null)
            {
                using (var reader = XmlReader.Create(relsEntry.Open(), ReaderSettings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship")
                        {
                            var id = reader.GetAttribute("Id");
                            var target = reader.GetAttribute("Target");
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                            {
                                relationships[id] = target;
                            }
                        }
                    }
                }
            }

            var sheets = new List<SheetInfo>();
            var workbookEntry = archive.GetEntry("xl/workbook.xml");
            if (workbookEntry == null)
            {
                return sheets;
            }

            using (var reader = XmlReader.Create(workbookEntry.Open(), ReaderSettings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
                    {
                        continue;
                    }

                    var name = reader.GetAttribute("name") ?? string.Empty;
                    var relationshipId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                                         ?? reader.GetAttribute("r:id");
                    string target = null;
                    if (relationshipId != null)
                    {
                        relationships.TryGetValue(relationshipId, out target);
                    }

                    if (target == null)
                    {
                        target = "worksheets/sheet" + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture) + ".xml";
                    }

                    sheets.Add(new SheetInfo { Name = name, Path = ResolvePartPath(target) });
                }
            }

            return sheets;
        }

        private static string ResolvePartPath(string target)
        {
            var normalized = target.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return normalized.Substring(1);
            }

            return "xl/" + normalized;
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var strings = new List<string>();
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return strings;
            }

            using (var reader = XmlReader.Create(entry.Open(), ReaderSettings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                    {
                        continue;
                    }

                    // Every text run of the entry, rich or plain. Reading an element's content
                    // leaves the reader on the node after it, so the loop only advances itself
                    // when it did not just read content.
                    var builder = new StringBuilder();
                    using (var item = reader.ReadSubtree())
                    {
                        var more = item.Read();
                        while (more)
                        {
                            if (item.NodeType == XmlNodeType.Element && item.LocalName == "t")
                            {
                                builder.Append(item.ReadElementContentAsString());
                                more = !item.EOF;
                            }
                            else
                            {
                                more = item.Read();
                            }
                        }
                    }

                    strings.Add(builder.ToString());
                }
            }

            return strings;
        }

        /// <summary>Cell style indexes whose number format is a date or time.</summary>
        private static HashSet<int> ReadDateStyles(ZipArchive archive)
        {
            var dateStyles = new HashSet<int>();
            var entry = archive.GetEntry("xl/styles.xml");
            if (entry == null)
            {
                return dateStyles;
            }

            var customDateFormats = new HashSet<int>();
            var styleFormatIds = new List<int>();
            using (var reader = XmlReader.Create(entry.Open(), ReaderSettings))
            {
                var inCellXfs = false;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "numFmt")
                    {
                        if (int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                            IsDateFormatCode(reader.GetAttribute("formatCode")))
                        {
                            customDateFormats.Add(id);
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "cellXfs")
                    {
                        inCellXfs = true;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                    {
                        inCellXfs = false;
                    }
                    else if (inCellXfs && reader.NodeType == XmlNodeType.Element && reader.LocalName == "xf")
                    {
                        int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var formatId);
                        styleFormatIds.Add(formatId);
                    }
                }
            }

            for (var i = 0; i < styleFormatIds.Count; i++)
            {
                if (BuiltInDateFormats.Contains(styleFormatIds[i]) || customDateFormats.Contains(styleFormatIds[i]))
                {
                    dateStyles.Add(i);
                }
            }

            return dateStyles;
        }

        private static bool IsDateFormatCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            var inQuotes = false;
            var inBracket = false;
            foreach (var c in code)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && c == '[')
                {
                    inBracket = true;
                }
                else if (!inQuotes && c == ']')
                {
                    inBracket = false;
                }
                else if (!inQuotes && !inBracket)
                {
                    var lower = char.ToLowerInvariant(c);
                    if (lower == 'y' || lower == 'm' || lower == 'd' || lower == 'h' || lower == 's')
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<List<string>> ReadSheetRows(ZipArchiveEntry entry, List<string> sharedStrings, HashSet<int> dateStyles)
        {
            var rows = new List<List<string>>();
            using (var reader = XmlReader.Create(entry.Open(), ReaderSettings))
            {
                List<string> row = null;
                var nextColumn = 0;
                var inSheetData = false;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheetData")
                    {
                        inSheetData = true;
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "sheetData")
                    {
                        break;
                    }

                    if (!inSheetData)
                    {
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                    {
                        row = new List<string>();
                        nextColumn = 0;
                        if (reader.IsEmptyElement)
                        {
                            rows.Add(row);
                            row = null;
                        }

                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && row != null)
                    {
                        rows.Add(row);
                        row = null;
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && row != null)
                    {
                        var reference = reader.GetAttribute("r");
                        var column = ColumnIndex(reference);
                        if (column < 0)
                        {
                            column = nextColumn;
                        }

                        var type = reader.GetAttribute("t") ?? string.Empty;
                        int.TryParse(reader.GetAttribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var style);
                        var text = reader.IsEmptyElement ? string.Empty : ReadCellText(reader, type, sharedStrings, dateStyles.Contains(style));

                        while (row.Count < column)
                        {
                            row.Add(string.Empty);
                        }

                        if (row.Count == column)
                        {
                            row.Add(text);
                        }
                        else
                        {
                            row[column] = text;
                        }

                        nextColumn = column + 1;
                    }
                }
            }

            return rows;
        }

        private static string ReadCellText(XmlReader reader, string type, List<string> sharedStrings, bool isDate)
        {
            string value = null;
            string inline = null;
            using (var cell = reader.ReadSubtree())
            {
                var more = cell.Read();
                while (more)
                {
                    if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v")
                    {
                        value = cell.ReadElementContentAsString();
                        more = !cell.EOF;
                    }
                    else if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "t")
                    {
                        inline = (inline ?? string.Empty) + cell.ReadElementContentAsString();
                        more = !cell.EOF;
                    }
                    else
                    {
                        more = cell.Read();
                    }
                }
            }

            switch (type)
            {
                case "s":
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                           index >= 0 && index < sharedStrings.Count
                        ? sharedStrings[index]
                        : string.Empty;
                case "inlineStr":
                    return inline ?? string.Empty;
                case "b":
                    return value == "1" ? "true" : value == "0" ? "false" : value ?? string.Empty;
                case "str":
                case "e":
                    return value ?? string.Empty;
                default:
                    if (isDate && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                    {
                        try
                        {
                            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                        }
                        catch (ArgumentException)
                        {
                            return value;
                        }
                    }

                    return value ?? string.Empty;
            }
        }

        /// <summary>The zero-based column of a cell reference such as "C7"; -1 when it has none.</summary>
        internal static int ColumnIndex(string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return -1;
            }

            var index = 0;
            var letters = 0;
            foreach (var c in reference)
            {
                if (c >= 'A' && c <= 'Z')
                {
                    index = index * 26 + (c - 'A' + 1);
                    letters++;
                }
                else if (c >= 'a' && c <= 'z')
                {
                    index = index * 26 + (c - 'a' + 1);
                    letters++;
                }
                else
                {
                    break;
                }
            }

            return letters == 0 ? -1 : index - 1;
        }
    }
}
