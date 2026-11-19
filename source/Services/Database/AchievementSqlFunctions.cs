using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using SqlNado;
using System;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// SQL functions that run the plugin's C# rules inside SQLite, so plugin queries can score and
    /// bucket rows without loading them: <c>pa_prestige(percent, rarity)</c>,
    /// <c>pa_collection(rarity)</c>, <c>pa_localday(unlockTimeUtc)</c>,
    /// <c>pa_search(query, text, ...)</c>, <c>pa_classified(categoryKind, tags, key, ...)</c>, and
    /// the sort and label keys <c>pa_raritysort(percent, rarity)</c>, <c>pa_category(category)</c>,
    /// <c>pa_kindsort(categoryKind)</c> and <c>pa_tagcount(tags)</c>, and the collation
    /// <c>pa_ignorecase</c>.
    /// </summary>
    /// <remarks>
    /// They exist only on the plugin's own connections. No view may call them: other add-ons read
    /// the views on connections that lack them.
    /// </remarks>
    internal static class AchievementSqlFunctions
    {
        public static void Register(SQLiteDatabase db)
        {
            db.SetScalarFunction("pa_prestige", 2, true, context => context.SetResult(
                AchievementScoreCalculator.GetPrestigeValue(Double(context.Values[0]), ParseStoredRarity(Text(context.Values[1])))));
            db.SetScalarFunction("pa_collection", 1, true, context => context.SetResult(
                AchievementScoreCalculator.GetCollectionValue(ParseStoredRarity(Text(context.Values[0])))));
            db.SetScalarFunction("pa_localday", 1, true, context =>
            {
                var unlockUtc = ParseUtc(Text(context.Values[0]));
                context.SetResult(unlockUtc.HasValue
                    ? UnlockDayCounts.DayOf(unlockUtc.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null);
            });

            // 1 when the values, joined as SearchTextBuilder joins them, match the query as SearchQuery
            // matches it. A trimmed query holds no line break, so it matches the joined text exactly
            // when it matches one value; each value is read only until one matches.
            db.SetScalarFunction("pa_search", -1, true, context =>
            {
                var query = SearchQuery.From(Text(context.Values[0]));
                var matches = !query.HasValue;
                for (var index = 1; !matches && index < context.Values.Length; index++)
                {
                    var value = Text(context.Values[index]);
                    matches = !string.IsNullOrEmpty(value) && query.Matches(value);
                }

                context.SetResult(matches ? 1 : 0);
            });

            // 1 when any key matches the kind or tags as AchievementClassificationFilter matches it.
            db.SetScalarFunction("pa_classified", -1, true, context =>
            {
                var kind = Text(context.Values[0]);
                var tags = Text(context.Values[1]);
                context.SetResult(context.Values.Skip(2).Any(key => AchievementClassificationFilter.Matches(kind, tags, Text(key))) ? 1 : 0);
            });

            // The grid sort keys of AchievementSortHelper and the category label rows display.
            db.SetScalarFunction("pa_raritysort", 2, true, context => context.SetResult(
                AchievementRarityResolver.GetSortValue(Double(context.Values[0]), ParseStoredRarity(Text(context.Values[1])))));
            db.SetScalarFunction("pa_category", 1, true, context => context.SetResult(
                AchievementCategoryHelper.NormalizeCategoryOrDefault(Text(context.Values[0]))));
            db.SetScalarFunction("pa_kindsort", 1, true, context => context.SetResult(
                CategoryKindHelper.ToSortKey(Text(context.Values[0]))));
            db.SetScalarFunction("pa_tagcount", 1, true, context => context.SetResult(
                AchievementTagHelper.GetComponents(Text(context.Values[0])).Count));

            // The order of StringComparer.OrdinalIgnoreCase, which NOCASE folds only for ASCII letters.
            db.SetCollationFunction("pa_ignorecase", StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A stored rarity name as a tier; Common when missing or unknown.</summary>
        internal static RarityTier ParseStoredRarity(string value)
        {
            return RarityTierExtensions.TryParse(value, out var rarity)
                ? rarity
                : RarityTier.Common;
        }

        /// <summary>A stored timestamp as a UTC instant; null when missing or unparseable.</summary>
        internal static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return DateTimeUtilities.AsUtcKind(parsed);
            }

            return null;
        }

        private static double? Double(SQLiteValue value)
        {
            switch (value.Type)
            {
                case SQLiteColumnType.REAL:
                    return value.DoubleValue;
                case SQLiteColumnType.INTEGER:
                    return value.Int64Value;
                default:
                    return null;
            }
        }

        private static string Text(SQLiteValue value) =>
            value.Type == SQLiteColumnType.NULL ? null : value.StringValue;
    }
}
