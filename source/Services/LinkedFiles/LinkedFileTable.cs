using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// A linked file read as a table: named columns and rows of text cells. Every row has one
    /// cell per column, empty where the file had none, so readers of any format hand the mapping
    /// the same shape.
    /// </summary>
    public sealed class LinkedFileTable
    {
        public static readonly LinkedFileTable Empty = new LinkedFileTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        private readonly Dictionary<string, int> _indexByName;

        private LinkedFileTable(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            Columns = columns;
            Rows = rows;
            _indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                if (!_indexByName.ContainsKey(columns[i]))
                {
                    _indexByName[columns[i]] = i;
                }
            }
        }

        public IReadOnlyList<string> Columns { get; }

        public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

        /// <summary>
        /// Builds a table from column names and rows, making names unique (a repeat gets a
        /// " (2)" suffix), naming any column a row has beyond the header by its 1-based position,
        /// and padding every row to the column count.
        /// </summary>
        public static LinkedFileTable Build(IEnumerable<string> columns, IEnumerable<IEnumerable<string>> rows)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns ?? Enumerable.Empty<string>())
            {
                var name = (column ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    name = (names.Count + 1).ToString(CultureInfo.InvariantCulture);
                }

                names.Add(UniqueName(name, seen));
            }

            var materialized = new List<List<string>>();
            foreach (var row in rows ?? Enumerable.Empty<IEnumerable<string>>())
            {
                var cells = (row ?? Enumerable.Empty<string>()).Select(cell => cell ?? string.Empty).ToList();
                while (names.Count < cells.Count)
                {
                    names.Add(UniqueName((names.Count + 1).ToString(CultureInfo.InvariantCulture), seen));
                }

                materialized.Add(cells);
            }

            var width = names.Count;
            var padded = new List<IReadOnlyList<string>>(materialized.Count);
            foreach (var cells in materialized)
            {
                while (cells.Count < width)
                {
                    cells.Add(string.Empty);
                }

                padded.Add(cells);
            }

            return new LinkedFileTable(names, padded);
        }

        /// <summary>
        /// The index of a column named by its header, or by its 1-based position as text when no
        /// header matches; -1 when neither does.
        /// </summary>
        public int IndexOfColumn(string nameOrIndex)
        {
            var key = (nameOrIndex ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return -1;
            }

            if (_indexByName.TryGetValue(key, out var index))
            {
                return index;
            }

            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) &&
                position >= 1 && position <= Columns.Count)
            {
                return position - 1;
            }

            return -1;
        }

        public string Cell(int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex >= Rows.Count || columnIndex < 0 || columnIndex >= Columns.Count)
            {
                return string.Empty;
            }

            return Rows[rowIndex][columnIndex] ?? string.Empty;
        }

        private static string UniqueName(string name, HashSet<string> seen)
        {
            var candidate = name;
            var suffix = 2;
            while (!seen.Add(candidate))
            {
                candidate = name + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }

            return candidate;
        }
    }

    public enum LinkedFileReadStatus
    {
        Ok,
        NoFilePath,
        FileMissing,
        FileUnstable,
        ReadFailed,
        SchemaInvalid,
        NoAchievements,
        NoRows
    }

    public sealed class LinkedFileReadResult
    {
        private LinkedFileReadResult(
            LinkedFileReadStatus status,
            LinkedFileTable table,
            string message,
            IReadOnlyList<string> warnings,
            IReadOnlyList<string> sheetNames,
            IReadOnlyList<string> rowPaths)
        {
            Status = status;
            Table = table ?? LinkedFileTable.Empty;
            Message = message;
            Warnings = warnings ?? Array.Empty<string>();
            SheetNames = sheetNames ?? Array.Empty<string>();
            RowPaths = rowPaths ?? Array.Empty<string>();
        }

        public LinkedFileReadStatus Status { get; }

        public bool Success => Status == LinkedFileReadStatus.Ok;

        public LinkedFileTable Table { get; }

        /// <summary>Why the read failed, for the log and the mapping window; null on success.</summary>
        public string Message { get; }

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>The workbook's sheet names, for the mapping window; empty for other formats.</summary>
        public IReadOnlyList<string> SheetNames { get; }

        /// <summary>
        /// The rows paths a JSON or TOML file offers, for the mapping window: every list and
        /// object in the document; empty for other formats.
        /// </summary>
        public IReadOnlyList<string> RowPaths { get; }

        public static LinkedFileReadResult Ok(
            LinkedFileTable table,
            IReadOnlyList<string> warnings = null,
            IReadOnlyList<string> sheetNames = null,
            IReadOnlyList<string> rowPaths = null)
        {
            return new LinkedFileReadResult(LinkedFileReadStatus.Ok, table, null, warnings, sheetNames, rowPaths);
        }

        public static LinkedFileReadResult Failed(
            LinkedFileReadStatus status,
            string message,
            IReadOnlyList<string> sheetNames = null,
            IReadOnlyList<string> rowPaths = null)
        {
            return new LinkedFileReadResult(status, null, message, null, sheetNames, rowPaths);
        }
    }
}
