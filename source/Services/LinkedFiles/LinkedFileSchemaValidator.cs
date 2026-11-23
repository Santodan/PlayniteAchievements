using PlayniteAchievements.Models.Settings;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    public enum LinkedFileSchemaProblem
    {
        /// <summary>There is no mapping at all.</summary>
        NoColumns,

        /// <summary>No column is chosen to say which achievement a row is.</summary>
        NoMatchColumn,

        /// <summary>Creating achievements for unmatched rows needs an API name or a name column.</summary>
        CreateUnmatchedNeedsNameOrApiName,

        /// <summary>The custom time format is blank.</summary>
        CustomTimeFormatMissing
    }

    /// <summary>What a mapping still lacks before a file can be applied with it.</summary>
    public static class LinkedFileSchemaValidator
    {
        public static IReadOnlyList<LinkedFileSchemaProblem> Validate(LinkedFileSchema schema)
        {
            var problems = new List<LinkedFileSchemaProblem>();
            if (schema == null)
            {
                problems.Add(LinkedFileSchemaProblem.NoColumns);
                return problems;
            }

            // Unlock state needs no column of its own: a row means unlocked when no column says
            // otherwise. Which achievement a row is needs the match column.
            if (string.IsNullOrWhiteSpace(schema.MatchColumn))
            {
                problems.Add(LinkedFileSchemaProblem.NoMatchColumn);
            }

            if (schema.CreateUnmatchedRows && !NamesNewAchievements(schema))
            {
                problems.Add(LinkedFileSchemaProblem.CreateUnmatchedNeedsNameOrApiName);
            }

            if (schema.TimeFormat == LinkedFileTimeFormat.Custom && string.IsNullOrWhiteSpace(schema.CustomTimeFormat))
            {
                problems.Add(LinkedFileSchemaProblem.CustomTimeFormatMissing);
            }

            return problems;
        }

        public static bool IsValid(LinkedFileSchema schema)
        {
            return Validate(schema).Count == 0;
        }

        /// <summary>
        /// Whether a new achievement gets an API name or a name from the row: a column mapped to
        /// either, or the match column when it is read as one.
        /// </summary>
        public static bool NamesNewAchievements(LinkedFileSchema schema)
        {
            return schema.HasColumn(LinkedFileField.ApiName) ||
                   schema.HasColumn(LinkedFileField.DisplayName) ||
                   !string.IsNullOrWhiteSpace(schema.MatchColumn) &&
                   (schema.MatchBy == LinkedFileMatchBy.ApiName || schema.MatchBy == LinkedFileMatchBy.Name);
        }
    }
}
