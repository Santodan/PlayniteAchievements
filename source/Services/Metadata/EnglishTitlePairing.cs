using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// The last way a source's rows are paired with a game's achievements, after IDs, titles and
    /// the titles other sources matched: both sides restated in English, each row keeping its own
    /// ApiName, so a game shown in German pairs with a site that lists it in English (or translates
    /// it differently). The English text is only compared, never shown.
    /// </summary>
    internal static class EnglishTitlePairing
    {
        /// <summary>True when <paramref name="language"/> (the plugin's language setting) is English, or unset.</summary>
        public static bool IsEnglish(string language) =>
            string.IsNullOrWhiteSpace(language) ||
            string.Equals(language.Trim(), "english", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Pairs <paramref name="targets"/> with <paramref name="rows"/> by their English text.
        /// <paramref name="englishTargets"/> and <paramref name="englishRows"/> are the same
        /// achievements in English, joined to them by ApiName; a null <paramref name="englishRows"/>
        /// means the rows are already in English. An achievement or row with no English counterpart
        /// takes no part. The matches name the original achievements and rows.
        /// </summary>
        public static List<AchievementMatch> Match(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> englishTargets,
            IList<AchievementDetail> rows,
            IList<AchievementDetail> englishRows)
        {
            var matches = new List<AchievementMatch>();
            if (targets == null || targets.Count == 0 || rows == null || rows.Count == 0 || englishTargets == null)
            {
                return matches;
            }

            var originalOf = new Dictionary<AchievementDetail, AchievementDetail>();
            var targetStandIns = Restate(targets, ByApiName(englishTargets), originalOf);
            var rowStandIns = englishRows == null
                ? rows.Where(row => row != null).ToList()
                : Restate(rows, ByApiName(englishRows), originalOf);
            if (targetStandIns.Count == 0 || rowStandIns.Count == 0)
            {
                return matches;
            }

            foreach (var match in CrossSourceAchievementMatcher.Match(targetStandIns, rowStandIns))
            {
                matches.Add(new AchievementMatch(
                    originalOf.TryGetValue(match.Target, out var target) ? target : match.Target,
                    originalOf.TryGetValue(match.Source, out var source) ? source : match.Source));
            }

            return matches;
        }

        /// <summary>
        /// Each row with an English counterpart, as a stand-in carrying the English title and
        /// description and the row's own ApiName, trophy type and points.
        /// </summary>
        private static List<AchievementDetail> Restate(
            IEnumerable<AchievementDetail> rows,
            IReadOnlyDictionary<string, AchievementDetail> english,
            IDictionary<AchievementDetail, AchievementDetail> originalOf)
        {
            var standIns = new List<AchievementDetail>();
            foreach (var row in rows)
            {
                var apiName = row?.ApiName?.Trim();
                if (string.IsNullOrEmpty(apiName) ||
                    !english.TryGetValue(apiName, out var text) ||
                    string.IsNullOrWhiteSpace(text?.DisplayName))
                {
                    continue;
                }

                var standIn = new AchievementDetail
                {
                    ApiName = row.ApiName,
                    DisplayName = text.DisplayName,
                    Description = text.Description,
                    TrophyType = row.TrophyType,
                    Points = row.Points
                };
                standIns.Add(standIn);
                originalOf[standIn] = row;
            }

            return standIns;
        }

        private static Dictionary<string, AchievementDetail> ByApiName(IEnumerable<AchievementDetail> rows)
        {
            var byApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows ?? Enumerable.Empty<AchievementDetail>())
            {
                var apiName = row?.ApiName?.Trim();
                if (!string.IsNullOrEmpty(apiName) && !byApiName.ContainsKey(apiName))
                {
                    byApiName[apiName] = row;
                }
            }

            return byApiName;
        }
    }
}
