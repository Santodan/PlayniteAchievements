using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services
{
    /// <summary>Records split from delimited text, with the separator they were read with.</summary>
    internal sealed class DelimitedRecords
    {
        public List<List<string>> Rows { get; } = new List<List<string>>();

        /// <summary>Records written with another separator than the file's, by index.</summary>
        public HashSet<int> MixedRows { get; } = new HashSet<int>();

        public char Delimiter { get; set; } = ',';

        /// <summary>True when the file's first line named its separator (Excel's "sep=" line).</summary>
        public bool DeclaredBySeparatorLine { get; set; }
    }

    /// <summary>
    /// Quote-aware splitting of delimited text into records. Shared by the custom achievement
    /// importer, which only sees spreadsheet separators, and the linked file readers, which also
    /// accept pipes or an explicit separator.
    /// </summary>
    internal static class DelimitedTextReader
    {
        /// <summary>The separators a spreadsheet saves CSV with: comma, semicolon or tab.</summary>
        public static readonly char[] SpreadsheetDelimiters = { ',', ';', '\t' };

        /// <summary>The separators detected for a linked file: the spreadsheet ones plus pipe.</summary>
        public static readonly char[] LinkedFileDelimiters = { ',', ';', '\t', '|' };

        /// <summary>
        /// Splits the text into records with the one separator the file uses. Blank records are
        /// kept so a record's index stays its spreadsheet row; only a trailing blank one is
        /// dropped.
        /// </summary>
        /// <remarks>
        /// The separator is <paramref name="explicitDelimiter"/> when given; otherwise Excel's
        /// "sep=" line when the file starts with one, which Excel hides and which is therefore not
        /// a row; otherwise whichever candidate the header uses most. A record that has none of
        /// the separator but does have another candidate outside quotes was written with a
        /// different one, and is flagged rather than read as one cell.
        /// </remarks>
        public static DelimitedRecords Parse(string text, char? explicitDelimiter, IReadOnlyList<char> candidates)
        {
            var candidateSet = (candidates ?? SpreadsheetDelimiters).ToArray();
            var records = new DelimitedRecords();
            if (string.IsNullOrWhiteSpace(text))
            {
                records.Delimiter = explicitDelimiter ?? candidateSet.FirstOrDefault();
                return records;
            }

            if (text[0] == '﻿')
            {
                text = text.Substring(1);
            }

            if (TryReadSeparatorLine(ref text, out var declared, candidateSet))
            {
                records.Delimiter = explicitDelimiter ?? declared;
                records.DeclaredBySeparatorLine = true;
            }
            else
            {
                records.Delimiter = explicitDelimiter ?? DetectDelimiter(text, candidateSet);
            }

            var delimiter = records.Delimiter;
            var row = new List<string>();
            var cell = new StringBuilder();
            var inQuotes = false;
            var sawOtherDelimiter = false;

            void EndRow()
            {
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Count == 1 && sawOtherDelimiter)
                {
                    records.MixedRows.Add(records.Rows.Count);
                }

                records.Rows.Add(row);
                row = new List<string>();
                sawOtherDelimiter = false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                    continue;
                }

                if (c == delimiter)
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    EndRow();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (Array.IndexOf(candidateSet, c) >= 0)
                {
                    sawOtherDelimiter = true;
                }

                cell.Append(c);
            }

            if (cell.Length > 0 || row.Count > 0)
            {
                EndRow();
            }

            return records;
        }

        /// <summary>
        /// Reads and strips Excel's "sep=X" first line, which names the file's separator.
        /// </summary>
        public static bool TryReadSeparatorLine(ref string text, out char delimiter, IReadOnlyList<char> candidates)
        {
            delimiter = ',';
            if (text.Length < 5 || !text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = text[4];
            var lineEnd = 5;
            if (!candidates.Contains(candidate) ||
                (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n'))
            {
                return false;
            }

            if (lineEnd < text.Length && text[lineEnd] == '\r')
            {
                lineEnd++;
            }

            if (lineEnd < text.Length && text[lineEnd] == '\n')
            {
                lineEnd++;
            }

            delimiter = candidate;
            text = text.Substring(lineEnd);
            return true;
        }

        /// <summary>The candidate the first line uses most, ties going to the earlier candidate.</summary>
        public static char DetectDelimiter(string text, IReadOnlyList<char> candidates)
        {
            var lineEnd = text.IndexOfAny(new[] { '\r', '\n' });
            var headerText = lineEnd < 0 ? text : text.Substring(0, lineEnd);

            // Ties go to the earlier entry, so a one-column header reads as comma-separated.
            var best = candidates[0];
            var bestCount = CountOutsideQuotes(headerText, best);
            foreach (var candidate in candidates.Skip(1))
            {
                var count = CountOutsideQuotes(headerText, candidate);
                if (count > bestCount)
                {
                    best = candidate;
                    bestCount = count;
                }
            }

            return best;
        }

        public static int CountOutsideQuotes(string value, char delimiter)
        {
            var count = 0;
            var inQuotes = false;
            for (var i = 0; i < (value?.Length ?? 0); i++)
            {
                var c = value[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < value.Length && value[i + 1] == '"')
                    {
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (!inQuotes && c == delimiter)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
