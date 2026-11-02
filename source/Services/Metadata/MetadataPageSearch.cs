using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Finds a game's pages on a source by name, judging each page the search offers by how many
    /// of the game's achievements it matches rather than by how well its title reads, so a page
    /// with a similar title (an older release, one game of a collection) loses to the right one.
    /// A collection is also looked for game by game, each of its base categories by its own
    /// name against its own achievements, and the pages are kept together when they match more
    /// than the best single page.
    /// </summary>
    internal sealed class MetadataPageSearch
    {
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<MetadataPageCandidate>>> _candidates;
        private readonly Func<string, CancellationToken, Task<IList<AchievementDetail>>> _rows;
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<MetadataPageCandidate>>> _variantCandidates;
        private readonly Func<string, CancellationToken, Task<IList<AchievementDetail>>> _englishRows;

        /// <param name="candidates">The pages a search for a name offers, the source's likeliest first.</param>
        /// <param name="rows">A page's achievement rows; null or empty when it has none.</param>
        /// <param name="variantCandidates">
        /// The pages offered for a broader form of a name (see <see cref="Variants"/>), for a
        /// source that builds pages from names; <paramref name="candidates"/> when null.
        /// </param>
        /// <param name="englishRows">
        /// A page's rows in English, keyed by the same ApiNames; null when the rows already are.
        /// </param>
        public MetadataPageSearch(
            Func<string, CancellationToken, Task<IReadOnlyList<MetadataPageCandidate>>> candidates,
            Func<string, CancellationToken, Task<IList<AchievementDetail>>> rows,
            Func<string, CancellationToken, Task<IReadOnlyList<MetadataPageCandidate>>> variantCandidates = null,
            Func<string, CancellationToken, Task<IList<AchievementDetail>>> englishRows = null)
        {
            _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _variantCandidates = variantCandidates ?? candidates;
            _englishRows = englishRows;
        }

        /// <summary>
        /// The game's pages: the best page for <paramref name="names"/>, or the best page for each
        /// of <paramref name="subGames"/> when together they match more, then a page for each
        /// category those leave mostly unmatched. Null when no page matches enough of what it was
        /// searched for.
        /// </summary>
        /// <param name="categoryNames">Names other sources found a category's game under, searched after the category's own.</param>
        /// <param name="seeds">Pages another source pointed to (the same game on this platform), judged before any search.</param>
        /// <param name="englishTitles">
        /// The game's achievements in English (<see cref="MetadataLinkRequest.EnglishTitles"/>),
        /// against which the pages a search judged are judged again when none matched enough.
        /// </param>
        public async Task<MetadataPageChoice> ChooseAsync(
            IReadOnlyList<string> names,
            IList<AchievementDetail> targets,
            IReadOnlyList<MetadataSubGame> subGames,
            CancellationToken ct,
            IReadOnlyDictionary<string, IReadOnlyList<string>> categoryNames = null,
            IReadOnlyList<MetadataPageCandidate> seeds = null,
            IEnumerable<string> sharedNames = null,
            Func<CancellationToken, Task<IList<AchievementDetail>>> englishTitles = null)
        {
            _englishTitles = englishTitles;
            _categoryNames = categoryNames;
            _seeds = seeds;
            _shared = new HashSet<string>(
                (sharedNames ?? Enumerable.Empty<string>()).Concat((categoryNames ?? new Dictionary<string, IReadOnlyList<string>>()).Values.SelectMany(list => list ?? Array.Empty<string>())),
                StringComparer.OrdinalIgnoreCase);
            var chosen = await ChooseGamesAsync(names, targets, subGames, ct).ConfigureAwait(false);
            chosen = chosen == null ? null : await FillCategoriesAsync(chosen, targets, ct).ConfigureAwait(false);
            if (chosen != null)
            {
                chosen.Trace = string.Join("; ", chosen.Found) +
                               $" | {_judged} pages judged, {_searches} searches";
            }

            return chosen;
        }

        private Func<CancellationToken, Task<IList<AchievementDetail>>> _englishTitles;
        private IReadOnlyDictionary<string, IReadOnlyList<string>> _categoryNames;
        private IReadOnlyList<MetadataPageCandidate> _seeds;
        private HashSet<string> _shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _judged;
        private int _searches;

        /// <summary>A category's own name, then the names other sources found it under.</summary>
        private IReadOnlyList<string> NamesFor(string category)
        {
            var names = new List<string> { category };
            if (_categoryNames != null && category != null && _categoryNames.TryGetValue(category, out var known) && known != null)
            {
                names.AddRange(known);
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A category this size or larger that the chosen pages do not cover is looked for by its
        // own name; smaller ones (a single bonus achievement) are not worth a search each.
        private const int FillCategoryMinimum = 5;

        // A category counts as on the chosen pages when this many of its achievements match there
        // with descriptions this alike: a title shared by chance seldom shares its description.
        private const int CoveredCategoryMinimum = 2;
        private const double ConfirmingDescriptionSimilarity = 0.5;

        /// <summary>
        /// Adds a page for each category the chosen pages do not cover, found by the category's
        /// name (a DLC another platform lists on its own, such as "Monster Hunter World:
        /// Iceborne"), when that page matches more of it. A category with a few achievements on
        /// the chosen pages, their descriptions alike, is already there: the platform folded it
        /// into the game's list (The Binding of Isaac's one list on consoles, with a few awards of
        /// each DLC), so no list of its own is looked for.
        /// </summary>
        private async Task<MetadataPageChoice> FillCategoriesAsync(MetadataPageChoice chosen, IList<AchievementDetail> targets, CancellationToken ct)
        {
            if (chosen.Matched >= targets.Count)
            {
                return chosen;
            }

            var ids = chosen.Ids.ToList();
            var found = chosen.Names.ToList();
            var how = chosen.Found.ToList();
            var rows = new List<AchievementDetail>();
            foreach (var id in ids)
            {
                rows.AddRange(await _rows(id, ct).ConfigureAwait(false) ?? new List<AchievementDetail>());
            }

            var matches = CrossSourceAchievementMatcher.Match(targets, rows);
            var matched = new HashSet<AchievementDetail>(matches.Select(match => match.Target));
            var confirmed = new HashSet<AchievementDetail>(matches
                .Where(match => CrossSourceAchievementMatcher.DescriptionSimilarity(match.Target.Description, match.Source.Description) >= ConfirmingDescriptionSimilarity)
                .Select(match => match.Target));
            var categories = targets
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.Category))
                .GroupBy(row => row.Category.Trim(), CategoryPathHelper.LabelComparer)
                .Where(group => group.Count() >= FillCategoryMinimum && group.Count(confirmed.Contains) < CoveredCategoryMinimum)
                .ToList();
            var total = chosen.Matched;
            var uncovered = new List<string>();
            foreach (var category in categories)
            {
                var categoryRows = category.ToList();
                var page = await BestPageAsync(NamesFor(category.Key), categoryRows, "fill '" + category.Key + "'", ct).ConfigureAwait(false);
                var before = categoryRows.Count(matched.Contains);
                if (page == null || ids.Contains(page.Id, StringComparer.OrdinalIgnoreCase) || page.Matched <= before)
                {
                    if (before == 0)
                    {
                        uncovered.Add(category.Key);
                    }

                    continue;
                }

                ids.Add(page.Id);
                found.Add((category.Key, page.Title));
                how.Add(page.How);
                total += page.Matched - before;
            }

            var filled = ids.Count == chosen.Ids.Count ? chosen : new MetadataPageChoice(ids, total, found, how);
            filled.Uncovered = uncovered;
            return filled;
        }

        /// <summary>The best single page, or one page per game of a collection when together they match more.</summary>
        private async Task<MetadataPageChoice> ChooseGamesAsync(
            IReadOnlyList<string> names,
            IList<AchievementDetail> targets,
            IReadOnlyList<MetadataSubGame> subGames,
            CancellationToken ct)
        {
            var whole = await BestPageAsync(names, targets, "whole game", ct).ConfigureAwait(false);
            // One page that matches every achievement cannot be bettered game by game.
            if (subGames == null || subGames.Count < 2 || whole?.Matched >= targets.Count)
            {
                return Whole(whole);
            }

            var found = new Dictionary<MetadataSubGame, ScoredPage>();
            foreach (var subGame in subGames)
            {
                var page = await BestPageAsync(NamesFor(subGame.Name), subGame.Rows.ToList(), "game '" + subGame.Name + "'", ct).ConfigureAwait(false);
                if (page != null)
                {
                    found[subGame] = page;
                }
            }

            // A sibling's page title tells how the source names the collection's games ("FINAL
            // FANTASY X-2" found as "FINAL FANTASY X-2 HD Remaster"); the games not found are
            // looked for again named the same way.
            var affixes = found
                .Select(pair => Affix(pair.Key.Name, pair.Value.Title))
                .Where(affix => affix != null)
                .Distinct()
                .ToList();
            foreach (var subGame in subGames.Where(subGame => !found.ContainsKey(subGame)))
            {
                var affixed = affixes
                    .Select(affix => (affix.Value.Prefix + " " + Normalize(subGame.Name) + " " + affix.Value.Suffix).Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (affixed.Count == 0)
                {
                    continue;
                }

                var page = await BestPageAsync(affixed, subGame.Rows.ToList(), "game '" + subGame.Name + "' by a sibling's naming", ct).ConfigureAwait(false);
                if (page != null)
                {
                    found[subGame] = page;
                }
            }

            var ids = subGames
                .Where(found.ContainsKey)
                .Select(subGame => found[subGame].Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0)
            {
                return Whole(whole);
            }

            // Judged as the overlay will match it, list by list: the games' own counts overstate
            // pages that overlap (a collection's page beside its games' pages).
            var lists = new List<MetadataSourceList>();
            foreach (var id in ids)
            {
                lists.Add(new MetadataSourceList(id, id, (await _rows(id, ct).ConfigureAwait(false) ?? new List<AchievementDetail>()).ToList()));
            }

            var matched = MetadataOverlayRun.MatchByList(targets, lists.SelectMany(list => list.Rows).ToList(), lists, matchApiNames: false).Count;
            if (matched <= (whole?.Matched ?? 0))
            {
                return Whole(whole);
            }

            var chosen = subGames.Where(found.ContainsKey).ToList();
            return new MetadataPageChoice(
                ids,
                matched,
                chosen.Select(subGame => (subGame.Name, found[subGame].Title)).ToList(),
                chosen.Select(subGame => found[subGame].How).ToList());
        }

        private static MetadataPageChoice Whole(ScoredPage whole) =>
            whole == null ? null : new MetadataPageChoice(new[] { whole.Id }, whole.Matched, new[] { ((string)null, whole.Title) }, new[] { whole.How });

        /// <summary>
        /// The page that matches the most of <paramref name="targets"/> among those the names'
        /// searches offer, the first offered winning a tie. When none matches enough, simpler forms
        /// of the names are searched the same way, and when still none does, the pages judged are
        /// judged again in English.
        /// </summary>
        private async Task<ScoredPage> BestPageAsync(IReadOnlyList<string> names, IList<AchievementDetail> targets, string step, CancellationToken ct)
        {
            var judged = new List<(MetadataPageCandidate Candidate, string Via)>();
            return await BestNativePageAsync(names, targets, step, judged, ct).ConfigureAwait(false) ??
                   await BestInEnglishAsync(judged, targets, step, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The page among <paramref name="judged"/> that matches the most of
        /// <paramref name="targets"/> when the achievements left unmatched are compared in English;
        /// null when none matches enough, or there is no English to compare.
        /// </summary>
        private async Task<ScoredPage> BestInEnglishAsync(
            IReadOnlyList<(MetadataPageCandidate Candidate, string Via)> judged,
            IList<AchievementDetail> targets,
            string step,
            CancellationToken ct)
        {
            if (_englishTitles == null || judged.Count == 0)
            {
                return null;
            }

            var english = await _englishTitles(ct).ConfigureAwait(false);
            if (english == null || english.Count == 0)
            {
                return null;
            }

            ScoredPage best = null;
            foreach (var (candidate, via) in judged)
            {
                var rows = await _rows(candidate.Id, ct).ConfigureAwait(false);
                if (rows == null || rows.Count == 0)
                {
                    continue;
                }

                var englishRows = _englishRows == null ? null : await _englishRows(candidate.Id, ct).ConfigureAwait(false);
                if (_englishRows != null && (englishRows == null || englishRows.Count == 0))
                {
                    continue;
                }

                var native = CrossSourceAchievementMatcher.Match(targets, rows);
                var pairedTargets = new HashSet<AchievementDetail>(native.Select(match => match.Target));
                var pairedRows = new HashSet<AchievementDetail>(native.Select(match => match.Source));
                var matched = native.Count + EnglishTitlePairing.Match(
                    targets.Where(target => !pairedTargets.Contains(target)).ToList(),
                    english,
                    rows.Where(row => !pairedRows.Contains(row)).ToList(),
                    englishRows).Count;
                if (IsEnough(matched, targets.Count, rows.Count) && matched > (best?.Matched ?? 0))
                {
                    best = new ScoredPage(candidate.Id, candidate.Title, matched, $"{step}: {candidate.Origin ?? "candidate"} via {via}, in English");
                }

                if (best?.Matched >= targets.Count)
                {
                    break;
                }
            }

            return best;
        }

        private async Task<ScoredPage> BestNativePageAsync(
            IReadOnlyList<string> names,
            IList<AchievementDetail> targets,
            string step,
            List<(MetadataPageCandidate Candidate, string Via)> judged,
            CancellationToken ct)
        {
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScoredPage best = null;

            // Judges candidates into best; true once one matches every achievement, which cannot be bettered.
            async Task<bool> JudgeAsync(IEnumerable<MetadataPageCandidate> candidates, string via)
            {
                foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate?.Id) && tried.Add(candidate.Id)))
                {
                    _judged++;
                    var rows = await _rows(candidate.Id, ct).ConfigureAwait(false);
                    if (rows == null || rows.Count == 0)
                    {
                        continue;
                    }

                    judged.Add((candidate, via));

                    var matched = CrossSourceAchievementMatcher.Match(targets, rows).Count;
                    if (IsEnough(matched, targets.Count, rows.Count) && matched > (best?.Matched ?? 0))
                    {
                        best = new ScoredPage(candidate.Id, candidate.Title, matched, $"{step}: {candidate.Origin ?? "candidate"} via {via}");
                    }

                    if (best?.Matched >= targets.Count)
                    {
                        return true;
                    }
                }

                return false;
            }

            // Pages another source pointed to come first; they cost no search.
            if (await JudgeAsync(_seeds ?? Array.Empty<MetadataPageCandidate>(), "another source's page").ConfigureAwait(false))
            {
                return best;
            }

            var variants = Variants(names);
            foreach (var pass in new[] { names, variants })
            {
                foreach (var name in pass.Where(name => !string.IsNullOrWhiteSpace(name) && asked.Add(name.Trim())))
                {
                    _searches++;
                    var offer = ReferenceEquals(pass, variants) ? _variantCandidates : _candidates;
                    var candidates = await offer(name, ct).ConfigureAwait(false) ?? Array.Empty<MetadataPageCandidate>();
                    var kind = ReferenceEquals(pass, variants) ? "variant" : _shared.Contains(name) ? "shared name" : "name";
                    // A broader name is only tried while none before it found a page.
                    if (await JudgeAsync(candidates, $"{kind} '{name}'").ConfigureAwait(false) ||
                        (ReferenceEquals(pass, variants) && best != null))
                    {
                        return best;
                    }
                }

                if (best != null)
                {
                    return best;
                }
            }

            return null;
        }

        /// <summary>The overlay's test for a page found by name, against the shorter of the two lists.</summary>
        internal static bool IsEnough(int matched, int targets, int rows) =>
            matched > 0 && matched >= Math.Min(targets, rows) * MetadataOverlayRun.MinNameMatchShare;

        /// <summary>
        /// Broader forms of the names, for a source whose search finds nothing that matches under
        /// the full one, broadest last: the part after a colon or dash, without brackets,
        /// punctuation as spaces ("Re:Chain" as "Re Chain"), then with trailing words dropped one by
        /// one down to the first ("Mass Effect 1" as "Mass Effect", "Spyro the Dragon" as "Spyro",
        /// which finds the collection it is part of). Which page is right is still decided by the
        /// achievements.
        /// </summary>
        internal static IReadOnlyList<string> Variants(IEnumerable<string> names)
        {
            var variants = new List<string>();
            foreach (var name in (names ?? Enumerable.Empty<string>()).Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                // A series or edition before a colon or a spaced dash left off ("Legendary Edition:
                // Mass Effect 2", "Batman: Return to Arkham - Arkham City").
                foreach (var separator in new[] { ":", " - " })
                {
                    var at = name.LastIndexOf(separator, StringComparison.Ordinal);
                    var rest = at > 0 ? name.Substring(at + separator.Length).Trim() : string.Empty;
                    if (rest.Length > 0)
                    {
                        variants.Add(rest);
                    }
                }

                var words = Regex.Replace(Regex.Replace(name, @"[\(\[][^\)\]]*[\)\]]", " "), @"[^\p{L}\p{N}']+", " ")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (var dropped = 0; dropped < words.Length; dropped++)
                {
                    var variant = string.Join(" ", words.Take(words.Length - dropped));
                    if (!string.Equals(variant, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        variants.Add(variant);
                    }
                }
            }

            return variants.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// What a source's title adds around a game's name ("legendary edition" before, "hd
        /// remaster" after), in normalized words; null when the title does not contain the name.
        /// </summary>
        internal static (string Prefix, string Suffix)? Affix(string name, string title)
        {
            var normalizedName = Normalize(name);
            var normalizedTitle = Normalize(title);
            if (string.IsNullOrEmpty(normalizedName) || string.IsNullOrEmpty(normalizedTitle) ||
                string.Equals(normalizedName, normalizedTitle, StringComparison.Ordinal))
            {
                return null;
            }

            var padded = " " + normalizedTitle + " ";
            var index = padded.IndexOf(" " + normalizedName + " ", StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var prefix = padded.Substring(0, index).Trim();
            var suffix = padded.Substring(index + normalizedName.Length + 2).Trim();
            return (prefix, suffix);
        }

        private static string Normalize(string name) => CrossSourceAchievementMatcher.NormalizeText(name) ?? string.Empty;

        private sealed class ScoredPage
        {
            public ScoredPage(string id, string title, int matched, string how)
            {
                Id = id;
                Title = title;
                Matched = matched;
                How = how;
            }

            /// <summary>How the page was found, for the lookup's trace.</summary>
            public string How { get; }

            public string Id { get; }

            public string Title { get; }

            public int Matched { get; }
        }
    }
}
