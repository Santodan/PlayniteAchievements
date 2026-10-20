using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>One achievement paired with its counterpart in another source.</summary>
    internal readonly struct AchievementMatch
    {
        public AchievementMatch(AchievementDetail target, AchievementDetail source)
        {
            Target = target;
            Source = source;
        }

        public AchievementDetail Target { get; }

        public AchievementDetail Source { get; }
    }

    /// <summary>
    /// Pairs a game's achievements with another source's rows for the same game. An optional exact
    /// ApiName pass runs first, for sources that share the provider's identifiers; the rest are
    /// matched by normalized title, breaking duplicate titles by description, trophy type and
    /// points. A tie leaves the achievement unmatched, and each source row is used at most once.
    /// </summary>
    internal static class CrossSourceAchievementMatcher
    {
        public static List<AchievementMatch> Match(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> sources,
            Func<AchievementDetail, bool> sourceFilter = null,
            bool matchApiNames = false)
        {
            var matches = new List<AchievementMatch>();
            if (targets == null || targets.Count == 0 || sources == null || sources.Count == 0)
            {
                return matches;
            }

            var eligible = sources
                .Where(source => source != null && (sourceFilter == null || sourceFilter(source)))
                .ToList();
            if (eligible.Count == 0)
            {
                return matches;
            }

            var used = new HashSet<AchievementDetail>();
            var matchedTargets = new HashSet<AchievementDetail>();

            if (matchApiNames)
            {
                var byApiName = eligible
                    .Where(source => !string.IsNullOrWhiteSpace(source.ApiName))
                    .GroupBy(source => source.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var target in targets)
                {
                    var apiName = target?.ApiName?.Trim();
                    if (string.IsNullOrEmpty(apiName) ||
                        !byApiName.TryGetValue(apiName, out var source) ||
                        used.Contains(source))
                    {
                        continue;
                    }

                    matches.Add(new AchievementMatch(target, source));
                    used.Add(source);
                    matchedTargets.Add(target);
                }
            }

            var sourcesByTitle = eligible
                .Where(source => !used.Contains(source))
                .GroupBy(source => NormalizeText(source.DisplayName), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (sourcesByTitle.Count == 0)
            {
                return matches;
            }

            var targetTitleCounts = targets
                .Where(target => target != null && !matchedTargets.Contains(target))
                .GroupBy(target => NormalizeText(target.DisplayName), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                if (target == null || matchedTargets.Contains(target))
                {
                    continue;
                }

                var title = NormalizeText(target.DisplayName);
                if (string.IsNullOrWhiteSpace(title) || !sourcesByTitle.TryGetValue(title, out var candidates))
                {
                    continue;
                }

                var source = ResolveTitleMatch(target, title, targetTitleCounts, candidates, used);
                if (source == null)
                {
                    continue;
                }

                matches.Add(new AchievementMatch(target, source));
                used.Add(source);
            }

            return matches;
        }

        /// <summary>
        /// Share of IDs at which a cross-platform source is treated as reusing the provider's
        /// achievement IDs, so the exact ID pass runs before titles.
        /// </summary>
        public const double SharedApiNameThreshold = 0.6;

        /// <summary>
        /// The fraction of targets whose ID appears among the sources. Only IDs containing a letter
        /// count, on both sides: numeric IDs ("1", "2", ...) coincide between unrelated games.
        /// </summary>
        public static double ApiNameOverlap(IList<AchievementDetail> targets, IList<AchievementDetail> sources)
        {
            var sourceNames = new HashSet<string>(
                (sources ?? new List<AchievementDetail>())
                    .Select(source => source?.ApiName?.Trim())
                    .Where(IsDistinctiveApiName),
                StringComparer.OrdinalIgnoreCase);
            if (sourceNames.Count == 0)
            {
                return 0;
            }

            var candidates = (targets ?? new List<AchievementDetail>())
                .Select(target => target?.ApiName?.Trim())
                .Where(IsDistinctiveApiName)
                .ToList();
            return candidates.Count == 0
                ? 0
                : (double)candidates.Count(sourceNames.Contains) / candidates.Count;
        }

        private static bool IsDistinctiveApiName(string apiName)
        {
            return !string.IsNullOrEmpty(apiName) && apiName.Any(char.IsLetter);
        }

        /// <summary>
        /// Lowercase letters and digits without accents, with each run of anything else collapsed
        /// to one space. Apostrophes are dropped rather than spaced, so "Don't" reads as "dont".
        /// </summary>
        public static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var folded = GameNameNormalizer.StripDiacritics(value.Trim()).ToLowerInvariant();
            var chars = new char[folded.Length];
            var index = 0;
            var lastWasSpace = false;

            foreach (var c in folded)
            {
                if (GameNameNormalizer.IsApostrophe(c))
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    chars[index++] = c;
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    chars[index++] = ' ';
                    lastWasSpace = true;
                }
            }

            if (index > 0 && chars[index - 1] == ' ')
            {
                index--;
            }

            return new string(chars, 0, index);
        }

        private static AchievementDetail ResolveTitleMatch(
            AchievementDetail target,
            string title,
            IDictionary<string, int> targetTitleCounts,
            IList<AchievementDetail> candidates,
            ISet<AchievementDetail> used)
        {
            var available = candidates
                .Where(candidate => candidate != null && !used.Contains(candidate))
                .ToList();

            if (available.Count == 0)
            {
                return null;
            }

            var targetTitleCount = targetTitleCounts.TryGetValue(title, out var count) ? count : 0;
            if (targetTitleCount == 1 && available.Count == 1)
            {
                return available[0];
            }

            var scored = available
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = Score(target, candidate)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ToList();

            if (scored.Count == 0)
            {
                return null;
            }

            if (scored.Count > 1 && scored[0].Score == scored[1].Score)
            {
                return null;
            }

            return scored[0].Candidate;
        }

        private static int Score(AchievementDetail target, AchievementDetail source)
        {
            var score = 0;

            var targetDescription = NormalizeText(target?.Description);
            var sourceDescription = NormalizeText(source?.Description);
            if (!string.IsNullOrWhiteSpace(targetDescription) &&
                string.Equals(targetDescription, sourceDescription, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (!string.IsNullOrWhiteSpace(target?.TrophyType) &&
                string.Equals(target.TrophyType, source?.TrophyType, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (target?.Points != null &&
                source?.Points != null &&
                target.Points == source.Points)
            {
                score += 2;
            }

            return score;
        }
    }
}
