using PlayniteAchievements.Models.Settings;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Delimited text as a table: the schema's separator or a detected one, the first
    /// non-blank record as the header when the schema says so, blank records skipped.
    /// </summary>
    internal static class DelimitedTableReader
    {
        public static LinkedFileReadResult Read(string text, LinkedFileSchema schema)
        {
            var records = DelimitedTextReader.Parse(text, schema.DelimiterChar, DelimitedTextReader.LinkedFileDelimiters);
            var warnings = records.MixedRows
                .OrderBy(index => index)
                .Select(index => "Row " + (index + 1).ToString(CultureInfo.InvariantCulture) + " uses a different separator than the file.")
                .ToList();

            // A space separator reads a run of spaces as one, so aligned columns parse as written.
            IEnumerable<List<string>> parsed = records.Rows;
            if (records.Delimiter == ' ')
            {
                parsed = parsed.Select(row => row.Where(cell => !string.IsNullOrEmpty(cell)).ToList());
            }

            var rows = parsed.Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToList();
            if (rows.Count == 0)
            {
                return LinkedFileReadResult.Ok(LinkedFileTable.Empty, warnings);
            }

            IEnumerable<string> columns;
            if (schema.HasHeader)
            {
                columns = rows[0].Select(cell => (cell ?? string.Empty).Trim());
                rows = rows.Skip(1).ToList();
            }
            else
            {
                var width = rows.Max(row => row.Count);
                columns = Enumerable.Range(1, width).Select(index => index.ToString(CultureInfo.InvariantCulture));
            }

            return LinkedFileReadResult.Ok(LinkedFileTable.Build(columns, rows.Select(row => row.Select(cell => (cell ?? string.Empty).Trim()))), warnings);
        }
    }
}
