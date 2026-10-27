using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>One TrueGaming page of a game: the site, its slug and the parsed page.</summary>
    internal sealed class TrueGamingFoundPage
    {
        public TrueGamingFoundPage(TrueGamingSite site, string slug, TrueGamingPage page)
        {
            Site = site;
            Slug = slug;
            Page = page;
        }

        public TrueGamingSite Site { get; }

        public string Slug { get; }

        public TrueGamingPage Page { get; }
    }

    /// <summary>The rows a game's TrueGaming pages give together.</summary>
    internal sealed class TrueGamingCombinedRows
    {
        public TrueGamingCombinedRows(
            List<AchievementDetail> rows,
            Dictionary<string, AchievementDetail> aliases,
            int mergedByDescription,
            int mergedByPosition)
        {
            Rows = rows;
            Aliases = aliases;
            MergedByDescription = mergedByDescription;
            MergedByPosition = mergedByPosition;
        }

        public List<AchievementDetail> Rows { get; }

        /// <summary>Each listing's id on a later page to the row it joined, which goes by its first listing's id.</summary>
        public Dictionary<string, AchievementDetail> Aliases { get; }

        /// <summary>How many listings joined a row by description, their names differing between sites.</summary>
        public int MergedByDescription { get; }

        /// <summary>How many listings joined a row by their place on the page, name and description both differing.</summary>
        public int MergedByPosition { get; }
    }

    /// <summary>
    /// Which of a game's TrueGaming pages the tags need, and the rows they give together. Tags come
    /// from the pages in lookup order: each achievement takes its tags from the first page whose
    /// flags decoded and that lists it, with or without flags. A later page loads only for
    /// achievements no such page lists. Flags about one platform's version (Buggy, Platinum) are
    /// taken only from the game's own platform's site.
    /// Achievements of different pages are paired by name, then by description where the sites
    /// name one differently ("Grilled Tasty" on TrueSteamAchievements, "Grilled Tastes Better" on
    /// the others, both "Defeat Miltos"), then by their place on pages that list the same
    /// achievements in the same order ("The Dream Invader" on TrueAchievements, "The Dream
    /// Wanderer" on TrueTrophies, each the 42nd of 45).
    /// </summary>
    internal static class TrueGamingPageSet
    {
        /// <summary>
        /// True when <paramref name="site"/>'s page is still needed after <paramref name="found"/>:
        /// for achievements of <paramref name="targetNames"/> no found page with decoded flags lists.
        /// </summary>
        public static bool NeedsPage(
            TrueGamingSite site,
            MetadataField wanted,
            IEnumerable<TrueGamingPage> found,
            IEnumerable<string> targetNames)
        {
            if ((wanted & MetadataField.Tags) == 0)
            {
                return false;
            }

            var answered = new HashSet<string>(
                (found ?? Enumerable.Empty<TrueGamingPage>())
                    .Where(page => page.FlagsDecoded)
                    .SelectMany(page => page.Achievements)
                    .Select(row => NameKey(row.Name)),
                StringComparer.Ordinal);
            return (targetNames ?? Enumerable.Empty<string>())
                .Select(NameKey)
                .Where(key => key.Length > 0)
                .Any(key => !answered.Contains(key));
        }

        /// <summary>
        /// True when enough of the page's achievements share a name with the game's to be the same
        /// game, by the overlay's share for a game found by name.
        /// </summary>
        public static bool IsSameGame(TrueGamingPage page, IReadOnlyCollection<string> targetNames)
        {
            // Paired the way the overlay pairs them, so a page it would accept is not turned away
            // here (titles behind a collection's code, "TR2 | Snow March" against "TR Snow March").
            var targets = (targetNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new AchievementDetail { DisplayName = name })
                .ToList();
            var rows = (page?.Achievements ?? new List<TrueGamingAchievement>())
                .Where(row => !string.IsNullOrWhiteSpace(row?.Name))
                .Select(row => new AchievementDetail { DisplayName = row.Name })
                .ToList();
            if (targets.Count == 0 || rows.Count == 0)
            {
                return false;
            }

            var matched = global::PlayniteAchievements.Services.Metadata.CrossSourceAchievementMatcher.Match(targets, rows).Count;
            return global::PlayniteAchievements.Services.Metadata.MetadataPageSearch.IsEnough(matched, targets.Count, rows.Count);
        }

        /// <summary>The rows <paramref name="pages"/> give together, in page order; <paramref name="baseLabel"/> names the base game's category.</summary>
        public static List<AchievementDetail> Combine(IReadOnlyList<TrueGamingFoundPage> pages, string baseLabel, TrueGamingSite ownSite = null) =>
            CombineAll(pages, baseLabel, ownSite).Rows;

        /// <summary>
        /// <see cref="Combine"/>'s rows with the ids of the listings they joined. A page's row whose
        /// name no earlier page lists joins an earlier row by description when the description is
        /// one of a kind on both sides, the earlier row has no listing from this site yet, and both
        /// are in the same DLC pack. A row left over then joins the first page's row at its place
        /// (see <see cref="JoinByPosition"/>); otherwise it is a row of its own.
        /// <paramref name="ownSite"/> is the only site platform flags are taken from.
        /// </summary>
        public static TrueGamingCombinedRows CombineAll(IReadOnlyList<TrueGamingFoundPage> pages, string baseLabel, TrueGamingSite ownSite = null)
        {
            var entries = new List<List<(TrueGamingSite Site, TrueGamingAchievement Row)>>();
            var byName = new Dictionary<string, List<(TrueGamingSite Site, TrueGamingAchievement Row)>>(StringComparer.Ordinal);
            var mergedByDescription = 0;
            var mergedByPosition = 0;
            pages = pages ?? Array.Empty<TrueGamingFoundPage>();
            foreach (var found in pages)
            {
                var earlier = entries.ToList();
                var unnamed = new List<TrueGamingAchievement>();
                foreach (var row in found.Page.Achievements)
                {
                    var key = NameKey(row.Name);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (byName.TryGetValue(key, out var listings))
                    {
                        // One listing per site: a page naming two rows alike keeps its first.
                        if (listings.All(listing => listing.Site != found.Site))
                        {
                            listings.Add((found.Site, row));
                        }
                    }
                    else if (earlier.Count > 0)
                    {
                        unnamed.Add(row);
                    }
                    else
                    {
                        AddEntry(entries, byName, key, found.Site, row);
                    }
                }

                var pageDescriptions = DescriptionCounts(found.Page.Achievements.Select(row => row.Description));
                var earlierDescriptions = DescriptionCounts(earlier.Select(listings => listings[0].Row.Description));
                var leftovers = new List<TrueGamingAchievement>();
                foreach (var row in unnamed)
                {
                    var key = NameKey(row.Name);
                    if (byName.TryGetValue(key, out var named))
                    {
                        // Another row of this page by the same name came first.
                        if (named.All(listing => listing.Site != found.Site))
                        {
                            named.Add((found.Site, row));
                        }

                        continue;
                    }

                    var description = NameKey(row.Description);
                    var joined = description.Length > 0 &&
                                 pageDescriptions.TryGetValue(description, out var onPage) && onPage == 1 &&
                                 earlierDescriptions.TryGetValue(description, out var before) && before == 1
                        ? earlier.FirstOrDefault(listings =>
                            NameKey(listings[0].Row.Description) == description &&
                            listings.All(listing => listing.Site != found.Site) &&
                            SameDlc(listings[0].Row.Dlc, row.Dlc))
                        : null;
                    if (joined != null)
                    {
                        joined.Add((found.Site, row));
                        byName[key] = joined;
                        mergedByDescription++;
                    }
                    else
                    {
                        leftovers.Add(row);
                    }
                }

                var byPosition = JoinByPosition(pages[0], found, entries, leftovers);
                foreach (var row in leftovers)
                {
                    var key = NameKey(row.Name);
                    if (byPosition.TryGetValue(row, out var placed))
                    {
                        placed.Add((found.Site, row));
                        byName[key] = placed;
                        mergedByPosition++;
                    }
                    else if (byName.TryGetValue(key, out var named))
                    {
                        // Another leftover of this page by the same name came first.
                        if (named.All(listing => listing.Site != found.Site))
                        {
                            named.Add((found.Site, row));
                        }
                    }
                    else
                    {
                        AddEntry(entries, byName, key, found.Site, row);
                    }
                }
            }

            var decoded = new HashSet<TrueGamingAchievement>(pages.Where(found => found.Page.FlagsDecoded).SelectMany(found => found.Page.Achievements));
            var rows = new List<AchievementDetail>();
            var aliases = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var listings in entries)
            {
                var row = ToRow(listings, baseLabel, decoded, ownSite);
                rows.Add(row);
                foreach (var listing in listings.Skip(1))
                {
                    if (!string.IsNullOrWhiteSpace(listing.Row.Id) &&
                        !string.Equals(listing.Row.Id, row.ApiName, StringComparison.OrdinalIgnoreCase) &&
                        !aliases.ContainsKey(listing.Row.Id))
                    {
                        aliases[listing.Row.Id] = row;
                    }
                }
            }

            return new TrueGamingCombinedRows(rows, aliases, mergedByDescription, mergedByPosition);
        }

        /// <summary>
        /// The first page's rows <paramref name="leftovers"/> of <paramref name="found"/> join by
        /// place: only when both pages list as many rows, every row already joined across the two
        /// sits at the same place on both, and the row at a leftover's place has no listing from
        /// this site yet and is in the same DLC pack.
        /// </summary>
        private static Dictionary<TrueGamingAchievement, List<(TrueGamingSite Site, TrueGamingAchievement Row)>> JoinByPosition(
            TrueGamingFoundPage first,
            TrueGamingFoundPage found,
            List<List<(TrueGamingSite Site, TrueGamingAchievement Row)>> entries,
            List<TrueGamingAchievement> leftovers)
        {
            var joins = new Dictionary<TrueGamingAchievement, List<(TrueGamingSite Site, TrueGamingAchievement Row)>>();
            var firstRows = first.Page.Achievements;
            var rows = found.Page.Achievements;
            if (leftovers.Count == 0 || first.Site == found.Site || firstRows.Count != rows.Count)
            {
                return joins;
            }

            var entryAt = new Dictionary<int, List<(TrueGamingSite Site, TrueGamingAchievement Row)>>();
            foreach (var listings in entries)
            {
                var firstRow = listings.FirstOrDefault(listing => listing.Site == first.Site).Row;
                var at = firstRow == null ? -1 : firstRows.IndexOf(firstRow);
                if (at < 0)
                {
                    continue;
                }

                var row = listings.FirstOrDefault(listing => listing.Site == found.Site).Row;
                if (row != null && rows.IndexOf(row) != at)
                {
                    // The pages list the achievements in different orders.
                    return joins;
                }

                entryAt[at] = listings;
            }

            foreach (var row in leftovers)
            {
                if (entryAt.TryGetValue(rows.IndexOf(row), out var listings) &&
                    listings.All(listing => listing.Site != found.Site) &&
                    SameDlc(listings[0].Row.Dlc, row.Dlc))
                {
                    joins[row] = listings;
                }
            }

            return joins;
        }

        private static void AddEntry(
            List<List<(TrueGamingSite Site, TrueGamingAchievement Row)>> entries,
            Dictionary<string, List<(TrueGamingSite Site, TrueGamingAchievement Row)>> byName,
            string key,
            TrueGamingSite site,
            TrueGamingAchievement row)
        {
            var listings = new List<(TrueGamingSite Site, TrueGamingAchievement Row)> { (site, row) };
            byName[key] = listings;
            entries.Add(listings);
        }

        private static Dictionary<string, int> DescriptionCounts(IEnumerable<string> descriptions)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var key in descriptions.Select(NameKey).Where(key => key.Length > 0))
            {
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            return counts;
        }

        private static bool SameDlc(string left, string right) =>
            string.Equals(
                CategoryPathHelper.SanitizeSegment(left) ?? string.Empty,
                CategoryPathHelper.SanitizeSegment(right) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The combined row: tags from the first listing with decoded flags, without platform
        /// flags, plus the platform flags of the own site's listing.
        /// </summary>
        private static AchievementDetail ToRow(
            List<(TrueGamingSite Site, TrueGamingAchievement Row)> listings,
            string baseLabel,
            ISet<TrueGamingAchievement> decoded,
            TrueGamingSite ownSite)
        {
            var first = listings[0].Row;
            var dlc = CategoryPathHelper.SanitizeSegment(first.Dlc);
            var answer = listings.Select(listing => listing.Row).FirstOrDefault(decoded.Contains);
            var own = ownSite == null ? null : listings.FirstOrDefault(listing => listing.Site == ownSite && decoded.Contains(listing.Row)).Row;
            var flags = (answer?.Flags ?? new List<string>()).Where(flag => !TrueGamingFlags.IsPlatformFlag(flag))
                .Concat((own?.Flags ?? new List<string>()).Where(TrueGamingFlags.IsPlatformFlag))
                .ToList();
            return new AchievementDetail
            {
                ApiName = first.Id,
                DisplayName = first.Name,
                Description = first.Description,
                Category = string.IsNullOrWhiteSpace(dlc) ? baseLabel : dlc,
                CategoryKind = string.IsNullOrWhiteSpace(dlc) ? CategoryKindHelper.Base : CategoryKindHelper.Dlc,
                Tags = TrueGamingFlags.ToTags(flags),
                Points = listings.FirstOrDefault(listing => listing.Site == TrueGamingSite.TrueAchievements).Row?.Points,
                TrophyType = listings.FirstOrDefault(listing => listing.Site == TrueGamingSite.TrueTrophies).Row?.TrophyType
            };
        }

        /// <summary>A name compared across sites: its letters and digits, accents dropped and lowercased.</summary>
        internal static string NameKey(string name) =>
            new string(GameNameNormalizer.StripDiacritics(name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
