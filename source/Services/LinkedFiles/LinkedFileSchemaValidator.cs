using PlayniteAchievements.Models.Settings;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    public enum LinkedFileSchemaProblem
    {
        /// <summary>No column feeds unlock state, time or progress, and rows do not mean unlocked.</summary>
        NoUnlockSource,

        /// <summary>No API name or name column is mapped and row order is not in the match order.</summary>
        NoMatchPath,

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
                problems.Add(LinkedFileSchemaProblem.NoUnlockSource);
                problems.Add(LinkedFileSchemaProblem.NoMatchPath);
                return problems;
            }

            var hasApiName = schema.HasColumn(LinkedFileField.ApiName);
            var hasName = schema.HasColumn(LinkedFileField.DisplayName);
            var hasUnlockSource =
                schema.HasColumn(LinkedFileField.Unlocked) ||
                schema.HasColumn(LinkedFileField.UnlockTime) ||
                schema.HasColumn(LinkedFileField.ProgressNum) ||
                schema.RowPresenceMeansUnlocked ||
                (schema.CreateUnmatchedRows && hasName);
            if (!hasUnlockSource)
            {
                problems.Add(LinkedFileSchemaProblem.NoUnlockSource);
            }

            if (!hasApiName && !hasName && !schema.EffectiveMatchOrder.Contains(LinkedFileMatchKey.RowOrder))
            {
                problems.Add(LinkedFileSchemaProblem.NoMatchPath);
            }

            if (schema.CreateUnmatchedRows && !hasApiName && !hasName)
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
    }
}
