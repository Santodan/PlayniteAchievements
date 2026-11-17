using PlayniteAchievements.Models.Settings;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    public enum LinkedFileSchemaProblem
    {
        /// <summary>No column is mapped to any field.</summary>
        NoColumns,

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

            // Unlock state and matching need no setting of their own: a row means unlocked when
            // no column says otherwise, and rows pair by order when no name column is mapped.
            var hasApiName = schema.HasColumn(LinkedFileField.ApiName);
            var hasName = schema.HasColumn(LinkedFileField.DisplayName);
            if (schema.Columns == null || schema.Columns.All(mapping => mapping == null || mapping.Field == LinkedFileField.Ignore))
            {
                problems.Add(LinkedFileSchemaProblem.NoColumns);
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
