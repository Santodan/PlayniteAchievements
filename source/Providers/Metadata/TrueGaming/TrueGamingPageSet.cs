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

    /// <summary>
    /// Which of a game's TrueGaming pages the wanted fields need, and the rows they give together.
    /// Points come only from TrueAchievements and trophy ranks only from TrueTrophies. Tags and
    /// categories come from the pages in lookup order: each achievement takes its category from
    /// the first page that lists it, and its tags from the first page that lists it with flags.
    /// Achievements of different pages are paired by name.
    /// </summary>
    internal static class TrueGamingPageSet
    {
        /// <summary>
        /// True when <paramref name="site"/>'s page is still needed after <paramref name="found"/>:
        /// for points or ranks it alone supplies, or for achievements of <paramref name="targetNames"/>
        /// the found pages leave without tags or category.
        /// </summary>
        public static bool NeedsPage(
            TrueGamingSite site,
            MetadataField wanted,
            IEnumerable<TrueGamingPage> found,
            IEnumerable<string> targetNames)
        {
            if ((wanted & MetadataField.Points) != 0 && site == TrueGamingSite.TrueAchievements ||
                (wanted & MetadataField.TrophyTypes) != 0 && site == TrueGamingSite.TrueTrophies)
            {
                return true;
            }

            var wantsTags = (wanted & MetadataField.Tags) != 0;
            var wantsCategories = (wanted & (MetadataField.Categories | MetadataField.CategoryArt)) != 0;
            if (!wantsTags && !wantsCategories)
            {
                return false;
            }

            var rows = (found ?? Enumerable.Empty<TrueGamingPage>()).SelectMany(page => page.Achievements).ToList();
            var listed = new HashSet<string>(rows.Select(row => NameKey(row.Name)), StringComparer.Ordinal);
            var flagged = new HashSet<string>(rows.Where(row => row.Flags.Count > 0).Select(row => NameKey(row.Name)), StringComparer.Ordinal);
            return (targetNames ?? Enumerable.Empty<string>())
                .Select(NameKey)
                .Where(key => key.Length > 0)
                .Any(key => wantsTags && !flagged.Contains(key) || wantsCategories && !listed.Contains(key));
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
        public static List<AchievementDetail> Combine(IReadOnlyList<TrueGamingFoundPage> pages, string baseLabel)
        {
            var order = new List<string>();
            var byName = new Dictionary<string, List<(TrueGamingSite Site, TrueGamingAchievement Row)>>(StringComparer.Ordinal);
            foreach (var found in pages ?? Array.Empty<TrueGamingFoundPage>())
            {
                foreach (var row in found.Page.Achievements)
                {
                    var key = NameKey(row.Name);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!byName.TryGetValue(key, out var listings))
                    {
                        listings = new List<(TrueGamingSite Site, TrueGamingAchievement Row)>();
                        byName[key] = listings;
                        order.Add(key);
                    }

                    // One listing per site: a page naming two rows alike keeps its first.
                    if (listings.All(listing => listing.Site != found.Site))
                    {
                        listings.Add((found.Site, row));
                    }
                }
            }

            return order.Select(key => ToRow(byName[key], baseLabel)).ToList();
        }

        private static AchievementDetail ToRow(List<(TrueGamingSite Site, TrueGamingAchievement Row)> listings, string baseLabel)
        {
            var first = listings[0].Row;
            var dlc = CategoryPathHelper.SanitizeSegment(first.Dlc);
            var flagged = listings.Select(listing => listing.Row).FirstOrDefault(row => row.Flags.Count > 0);
            return new AchievementDetail
            {
                ApiName = first.Id,
                DisplayName = first.Name,
                Description = first.Description,
                Category = string.IsNullOrWhiteSpace(dlc) ? baseLabel : dlc,
                CategoryKind = string.IsNullOrWhiteSpace(dlc) ? CategoryKindHelper.Base : CategoryKindHelper.Dlc,
                Tags = flagged == null ? null : TrueGamingFlags.ToTags(flagged.Flags),
                Points = listings.FirstOrDefault(listing => listing.Site == TrueGamingSite.TrueAchievements).Row?.Points,
                TrophyType = listings.FirstOrDefault(listing => listing.Site == TrueGamingSite.TrueTrophies).Row?.TrophyType
            };
        }

        /// <summary>A name compared across sites: its letters and digits, accents dropped and lowercased.</summary>
        internal static string NameKey(string name) =>
            new string(GameNameNormalizer.StripDiacritics(name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
