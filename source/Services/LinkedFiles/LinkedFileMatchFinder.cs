using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>One way a file's rows could be matched: a column, how its cells are read, and what that pairs.</summary>
    public sealed class LinkedFileMatchCandidate
    {
        internal LinkedFileMatchCandidate(string column, LinkedFileMatchBy by, LinkedFileMatchResult result, bool fits)
        {
            Column = column;
            By = by;
            Result = result;
            Fits = fits;
        }

        public string Column { get; }

        public LinkedFileMatchBy By { get; }

        /// <summary>How many rows it pairs with an achievement.</summary>
        public int Matched => Result.Matches.Count;

        /// <summary>
        /// Whether it may be chosen without the user: any pairing for API names and names, and
        /// for positions only when every numbered row lands on its own achievement, or, for the
        /// position column, when the file has exactly one row per achievement.
        /// </summary>
        public bool Fits { get; }

        public bool IsPosition => By == LinkedFileMatchBy.PositionFromOne || By == LinkedFileMatchBy.PositionFromZero;

        public bool IsPositionColumn => string.Equals(Column, LinkedFileSchema.PositionColumn, StringComparison.Ordinal);

        internal LinkedFileMatchResult Result { get; }

        public bool Is(string column, LinkedFileMatchBy by) =>
            by == By && string.Equals(column, Column, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether it pairs the row with the achievement, which may come from another load of the list.</summary>
        public bool Pairs(int rowIndex, AchievementDetail achievement) =>
            achievement != null &&
            Result.Matches.Any(match => match.RowIndex == rowIndex &&
                                        string.Equals(match.Achievement.ApiName?.Trim(), achievement.ApiName?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tries every column of a file as the match column, read every way that suits its cells,
    /// so the Link File window can offer the ways that pair rows and choose the best one.
    /// </summary>
    public static class LinkedFileMatchFinder
    {
        /// <summary>The ways that pair at least one row, the best first.</summary>
        public static IReadOnlyList<LinkedFileMatchCandidate> Find(LinkedFileTable table, IReadOnlyList<AchievementDetail> achievements)
        {
            table = table ?? LinkedFileTable.Empty;
            var candidates = (achievements ?? Array.Empty<AchievementDetail>()).Where(achievement => achievement != null).ToList();
            var found = new List<LinkedFileMatchCandidate>();
            if (table.Rows.Count == 0 || candidates.Count == 0)
            {
                return found;
            }

            foreach (var column in table.Columns)
            {
                var isPositionColumn = string.Equals(column, LinkedFileSchema.PositionColumn, StringComparison.Ordinal);
                if (!isPositionColumn)
                {
                    Try(found, table, candidates, column, LinkedFileMatchBy.ApiName);
                    Try(found, table, candidates, column, LinkedFileMatchBy.Name);
                }

                if (isPositionColumn)
                {
                    Try(found, table, candidates, column, LinkedFileMatchBy.PositionFromOne, table.Rows.Count == candidates.Count);
                }
                else if (AllWholeNumbers(table, column))
                {
                    Try(found, table, candidates, column, LinkedFileMatchBy.PositionFromOne, true);
                    Try(found, table, candidates, column, LinkedFileMatchBy.PositionFromZero, true);
                }
            }

            // The one chosen without the user first, then the rest by what they pair.
            var best = Best(found);
            return found
                .OrderByDescending(candidate => ReferenceEquals(candidate, best))
                .ThenByDescending(candidate => candidate.Matched)
                .ThenBy(Rank)
                .ToList();
        }

        /// <summary>
        /// The way to choose without the user, or null when none fits. A column of API names or
        /// names wins over positions unless positions pair more than twice as many rows, since a
        /// name that matches is evidence and a position is only where a row sits.
        /// </summary>
        public static LinkedFileMatchCandidate Best(IReadOnlyList<LinkedFileMatchCandidate> candidates)
        {
            var fitting = (candidates ?? Array.Empty<LinkedFileMatchCandidate>()).Where(candidate => candidate.Fits).ToList();
            var named = fitting.Where(candidate => !candidate.IsPosition).OrderByDescending(candidate => candidate.Matched).ThenBy(Rank).FirstOrDefault();
            var placed = fitting.Where(candidate => candidate.IsPosition).OrderByDescending(candidate => candidate.Matched).ThenBy(Rank).FirstOrDefault();
            if (named == null)
            {
                return placed;
            }

            return placed != null && placed.Matched > named.Matched * 2 ? placed : named;
        }

        /// <summary>The ways that pair the row with the achievement, the best first.</summary>
        public static IReadOnlyList<LinkedFileMatchCandidate> Pairing(
            IReadOnlyList<LinkedFileMatchCandidate> candidates,
            int rowIndex,
            AchievementDetail achievement)
        {
            return (candidates ?? Array.Empty<LinkedFileMatchCandidate>())
                .Where(candidate => candidate.Pairs(rowIndex, achievement))
                .OrderByDescending(candidate => candidate.Matched)
                .ThenBy(Rank)
                .ToList();
        }

        private static void Try(
            List<LinkedFileMatchCandidate> found,
            LinkedFileTable table,
            IReadOnlyList<AchievementDetail> achievements,
            string column,
            LinkedFileMatchBy by,
            bool? fitsWhenAllPair = null)
        {
            var schema = new LinkedFileSchema { MatchColumn = column, MatchBy = by };
            var result = LinkedFileMatcher.Match(table, schema, achievements);
            if (result.Matches.Count == 0)
            {
                return;
            }

            bool fits;
            if (fitsWhenAllPair == null)
            {
                fits = true;
            }
            else if (string.Equals(column, LinkedFileSchema.PositionColumn, StringComparison.Ordinal))
            {
                fits = fitsWhenAllPair.Value;
            }
            else
            {
                // Every numbered row lands on an achievement of its own.
                var index = table.IndexOfColumn(column);
                var numberedRows = Enumerable.Range(0, table.Rows.Count).Count(row => !string.IsNullOrWhiteSpace(table.Cell(row, index)));
                fits = fitsWhenAllPair.Value && result.Matches.Count == numberedRows;
            }

            found.Add(new LinkedFileMatchCandidate(column, by, result, fits));
        }

        /// <summary>Whether every filled cell of the column is a whole number of 0 or more, and at least one is filled.</summary>
        private static bool AllWholeNumbers(LinkedFileTable table, string column)
        {
            var any = false;
            var index = table.IndexOfColumn(column);
            if (index < 0)
            {
                return false;
            }

            for (var row = 0; row < table.Rows.Count; row++)
            {
                var cell = table.Cell(row, index);
                if (string.IsNullOrWhiteSpace(cell))
                {
                    continue;
                }

                if (!LinkedFileValueParser.TryParseInt(cell, out var number) || number < 0)
                {
                    return false;
                }

                any = true;
            }

            return any;
        }

        /// <summary>The order ties break in: API names, names, a numbered column, then the position column.</summary>
        private static int Rank(LinkedFileMatchCandidate candidate)
        {
            switch (candidate.By)
            {
                case LinkedFileMatchBy.ApiName:
                    return 0;
                case LinkedFileMatchBy.Name:
                    return 1;
                default:
                    return candidate.IsPositionColumn ? 4 : candidate.By == LinkedFileMatchBy.PositionFromOne ? 2 : 3;
            }
        }
    }
}
