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

            // Each side's titles in three forms: as written, without the tag a collection repeats on
            // its titles after a separator ("Shenmue: Needle In A Haystack", "TR2 | Snow March"),
            // and without a first word repeated on many of them (a tag without a separator). A
            // side without a repeated tag reads its titles as written. Tags off both sides first,
            // then as written, then each form against the other side's others for what is still
            // unmatched: "TR Miu" meets "TR:UB | Miu", "TR The Cataclysm" meets "TR1 | The Cataclysm".
            Func<AchievementDetail, string> plain = row => NormalizeText(row.DisplayName);
            var targetForms = new[] { plain, UnprefixedTitleKey(targets) ?? plain, UnprefixedFirstWordKey(targets) ?? plain };
            var sourceForms = new[] { plain, UnprefixedTitleKey(eligible) ?? plain, UnprefixedFirstWordKey(eligible) ?? plain };
            var run = new HashSet<(Func<AchievementDetail, string>, Func<AchievementDetail, string>)>();
            foreach (var (t, s) in TitleFormOrder)
            {
                if (run.Add((targetForms[t], sourceForms[s])))
                {
                    MatchTitles(targets, eligible, used, matchedTargets, matches, targetForms[t], sourceForms[s]);
                }
            }

            MatchUniqueDescriptions(targets, eligible, used, matchedTargets, matches);
            return matches;
        }

        // The title forms paired in order (0 as written, 1 without a separated tag, 2 without a
        // repeated first word): tags off, as written, then every other pairing.
        private static readonly (int Target, int Source)[] TitleFormOrder =
        {
            (1, 1), (0, 0), (0, 1), (0, 2), (1, 0), (1, 2), (2, 0), (2, 1), (2, 2)
        };

        // How far the best of several same-titled candidates must lead the next to be taken.
        private const double ClearLead = 0.5;

        // A description pairs two achievements only when it has at least this many words.
        private const int DescriptionPairingMinimumWords = 3;

        /// <summary>
        /// Pairs what is still unmatched by description (a renamed or translated title), only where
        /// the description is written once among all of each side's rows: descriptions a game
        /// repeats ("Beat Mom's Heart 11 times" for each character) never pair on their own.
        /// </summary>
        private static void MatchUniqueDescriptions(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> eligible,
            ISet<AchievementDetail> used,
            ISet<AchievementDetail> matchedTargets,
            List<AchievementMatch> matches)
        {
            var sources = UniqueByDescription(eligible);
            if (sources.Count == 0)
            {
                return;
            }

            foreach (var pair in UniqueByDescription(targets))
            {
                if (matchedTargets.Contains(pair.Value) ||
                    !sources.TryGetValue(pair.Key, out var source) ||
                    used.Contains(source))
                {
                    continue;
                }

                matches.Add(new AchievementMatch(pair.Value, source));
                used.Add(source);
                matchedTargets.Add(pair.Value);
            }
        }

        private static Dictionary<string, AchievementDetail> UniqueByDescription(IEnumerable<AchievementDetail> rows)
        {
            return rows
                .Where(row => row != null)
                .Select(row => (Key: NormalizeText(row.Description), Row: row))
                .Where(entry => entry.Key != null && entry.Key.Split(' ').Length >= DescriptionPairingMinimumWords)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.OrdinalIgnoreCase);
        }

        private static void MatchTitles(
            IList<AchievementDetail> targets,
            IList<AchievementDetail> eligible,
            ISet<AchievementDetail> used,
            ISet<AchievementDetail> matchedTargets,
            List<AchievementMatch> matches,
            Func<AchievementDetail, string> targetKey,
            Func<AchievementDetail, string> sourceKey)
        {
            var sourcesByTitle = eligible
                .Where(source => !used.Contains(source))
                .GroupBy(sourceKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (sourcesByTitle.Count == 0)
            {
                return;
            }

            var targetTitleCounts = targets
                .Where(target => target != null && !matchedTargets.Contains(target))
                .GroupBy(targetKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                if (target == null || matchedTargets.Contains(target))
                {
                    continue;
                }

                var title = targetKey(target);
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
                matchedTargets.Add(target);
            }
        }

        // A title prefix counts as a collection's tag when this many titles carry it.
        private const int RepeatedPrefixMinimum = 3;

        /// <summary>
        /// The title key without a repeated prefix: the part before the last "|" or, without one,
        /// the first ":", when at least <see cref="RepeatedPrefixMinimum"/> of the rows carry the
        /// same one. Null when no prefix repeats, so titles are only matched as written.
        /// </summary>
        private static Func<AchievementDetail, string> UnprefixedTitleKey(IEnumerable<AchievementDetail> rows)
        {
            var repeated = new HashSet<string>(
                rows.Where(row => row != null)
                    .Select(row => SplitTitlePrefix(row.DisplayName).Prefix)
                    .Where(prefix => prefix != null)
                    .GroupBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() >= RepeatedPrefixMinimum)
                    .Select(group => group.Key),
                StringComparer.OrdinalIgnoreCase);
            if (repeated.Count == 0)
            {
                return null;
            }

            return row =>
            {
                var (prefix, remainder) = SplitTitlePrefix(row.DisplayName);
                return NormalizeText(prefix != null && repeated.Contains(prefix) ? remainder : row.DisplayName);
            };
        }

        /// <summary>
        /// The normalized title without its first word, when at least
        /// <see cref="RepeatedPrefixMinimum"/> of the rows start with that word and more follows it.
        /// Null when no first word repeats.
        /// </summary>
        private static Func<AchievementDetail, string> UnprefixedFirstWordKey(IEnumerable<AchievementDetail> rows)
        {
            var repeated = new HashSet<string>(
                rows.Where(row => row != null)
                    .Select(row => SplitFirstWord(NormalizeText(row.DisplayName)).Word)
                    .Where(word => word != null)
                    .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() >= RepeatedPrefixMinimum)
                    .Select(group => group.Key),
                StringComparer.OrdinalIgnoreCase);
            if (repeated.Count == 0)
            {
                return null;
            }

            return row =>
            {
                var title = NormalizeText(row.DisplayName);
                var (word, remainder) = SplitFirstWord(title);
                return word != null && repeated.Contains(word) ? remainder : title;
            };
        }

        private static (string Word, string Remainder) SplitFirstWord(string normalizedTitle)
        {
            var index = normalizedTitle?.IndexOf(' ') ?? -1;
            return index <= 0 || index >= normalizedTitle.Length - 1
                ? (null, null)
                : (normalizedTitle.Substring(0, index), normalizedTitle.Substring(index + 1));
        }

        private static (string Prefix, string Remainder) SplitTitlePrefix(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return (null, null);
            }

            var index = title.LastIndexOf('|');
            if (index <= 0)
            {
                index = title.IndexOf(':');
            }

            if (index <= 0 || index >= title.Length - 1)
            {
                return (null, null);
            }

            var prefix = title.Substring(0, index).Trim();
            var remainder = title.Substring(index + 1).Trim();
            return prefix.Length == 0 || remainder.Length == 0 ? (null, null) : (prefix, remainder);
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
        /// British and American spellings read alike (see <see cref="FoldEnglishSpelling"/>).
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

            return FoldEnglishSpelling(new string(chars, 0, index));
        }

        /// <summary>
        /// Folds British and American spellings together: "our" reads as "or" ("armour", "armor")
        /// and "iz"/"yz" as "is"/"ys" ("realize", "realise"; "analyze", "analyse"). Both texts
        /// being compared fold the same way, so a word the rule also touches ("four", "wizard")
        /// still compares equal to itself; no word list is needed.
        /// </summary>
        internal static string FoldEnglishSpelling(string text)
        {
            return string.IsNullOrEmpty(text)
                ? text
                : text.Replace("our", "or").Replace("iz", "is").Replace("yz", "ys");
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

            // A best candidate that is not clearly ahead (an eighth of the description score) is a tie.
            if (scored.Count > 1 && scored[0].Score - scored[1].Score < ClearLead)
            {
                return null;
            }

            return scored[0].Candidate;
        }

        /// <summary>
        /// How alike two rows with the same title are: description similarity (up to 4, so a
        /// reworded description still counts), then trophy type (3) and points (2).
        /// </summary>
        private static double Score(AchievementDetail target, AchievementDetail source)
        {
            var score = 4 * DescriptionSimilarity(target?.Description, source?.Description);

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

        /// <summary>
        /// The share of words two descriptions have in common (shared words over all their
        /// distinct words): 1 when they read alike, 0 when either is missing.
        /// </summary>
        internal static double DescriptionSimilarity(string first, string second)
        {
            var a = NormalizeText(first);
            var b = NormalizeText(second);
            if (a == null || b == null)
            {
                return 0;
            }

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            var wordsA = new HashSet<string>(a.Split(' '), StringComparer.OrdinalIgnoreCase);
            var wordsB = new HashSet<string>(b.Split(' '), StringComparer.OrdinalIgnoreCase);
            var shared = wordsA.Count(wordsB.Contains);
            return (double)shared / (wordsA.Count + wordsB.Count - shared);
        }
    }
}
