using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Fetches a link that names several lists (a collection's games) one list at a time and
    /// joins them into one source set, keeping each list's rows apart in
    /// <see cref="AchievementSourceSet.Lists"/>. A link to one list is fetched as it is.
    /// </summary>
    internal static class MetadataListFetch
    {
        /// <summary>
        /// The source's rows for every list of the link, in the link's order; null when any list
        /// returned nothing, so the game keeps its previous values rather than losing one list's.
        /// </summary>
        /// <param name="linkLabel">The link's label, whose found names name each list's base category.</param>
        public static async Task<AchievementSourceSet> FetchAsync(
            IMetadataProvider provider,
            MetadataLinkRequest request,
            string linkId,
            ILogger logger,
            CancellationToken ct,
            string linkLabel = null)
        {
            var shown = MetadataLinkIds.ShownLink(provider as IMetadataLinkDetails, linkId) ?? linkId;
            var parts = MetadataLinkIds.Parse(shown);
            if (parts.Count <= 1)
            {
                var one = parts.Count == 1 && (linkId ?? string.Empty).IndexOf(MetadataLinkIds.Separator) >= 0 ? parts[0] : linkId;
                return await provider.FetchAsync(request, one, ct).ConfigureAwait(false);
            }

            var fetched = new List<(string Id, AchievementSourceSet Set)>();
            foreach (var part in parts)
            {
                var set = await provider.FetchAsync(request, part, ct).ConfigureAwait(false);
                if (set == null || set.Achievements.Count == 0)
                {
                    logger?.Debug($"[Metadata] {provider.Key} returned nothing for '{request?.Game?.Name}' list {part} of {linkId}.");
                    return null;
                }

                fetched.Add((string.IsNullOrWhiteSpace(set.ExtendedLinkId) ? part : set.ExtendedLinkId.Trim(), set));
            }

            NameBaseCategories(fetched, MetadataFoundNames.Parse(linkLabel), request?.BaseCategoryLabel, request?.Game?.Name);
            return Combine(provider.Key, linkId, parts, fetched);
        }

        /// <summary>
        /// Files each list's base-game rows under that list's game rather than the whole game's
        /// name, so a collection's games stay apart as categories: by the category the list was
        /// found for, else its found title, else the source's title for it. The names are the
        /// label's lines by position, so a label with another count names none. DLC rows, and base
        /// rows the source already named after a game of its own, keep their categories.
        /// </summary>
        internal static void NameBaseCategories(
            IReadOnlyList<(string Id, AchievementSourceSet Set)> fetched,
            IReadOnlyList<(string Category, string Title)> foundNames,
            string baseLabel,
            string gameName)
        {
            if (fetched == null || fetched.Count < 2)
            {
                return;
            }

            var wholeGame = new[] { baseLabel, gameName, CategoryPathHelper.SanitizeSegment(gameName) }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToList();
            var byPosition = foundNames != null && foundNames.Count == fetched.Count;
            for (var i = 0; i < fetched.Count; i++)
            {
                var set = fetched[i].Set;
                var name = CategoryPathHelper.SanitizeSegment(
                    byPosition
                        ? foundNames[i].Category ?? foundNames[i].Title
                        : set?.Title);
                if (string.IsNullOrWhiteSpace(name) || set == null)
                {
                    continue;
                }

                foreach (var row in set.Achievements)
                {
                    if (row != null &&
                        string.Equals(row.CategoryKind, CategoryKindHelper.Base, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(row.Category) ||
                         wholeGame.Any(label => string.Equals(label, row.Category.Trim(), StringComparison.OrdinalIgnoreCase))))
                    {
                        row.Category = name.Trim();
                    }
                }
            }
        }

        /// <summary>The lists' rows as one set: in link order, with each list's own rows, title and art.</summary>
        internal static AchievementSourceSet Combine(
            string sourceKey,
            string linkId,
            IReadOnlyList<string> requestedIds,
            IReadOnlyList<(string Id, AchievementSourceSet Set)> fetched)
        {
            var combined = new AchievementSourceSet(
                sourceKey,
                linkId,
                fetched.SelectMany(list => list.Set.Achievements).ToList())
            {
                SharesApiNames = fetched.All(list => list.Set.SharesApiNames),
                ApiNamesMayBeShared = fetched.Any(list => list.Set.ApiNamesMayBeShared),
                UnavailableFields = fetched.Aggregate(MetadataField.None, (fields, list) => fields | list.Set.UnavailableFields),
                TextInPluginLanguage = fetched.All(list => list.Set.TextInPluginLanguage),
                Lists = fetched
                    .Select(list => new MetadataSourceList(
                        list.Id,
                        string.IsNullOrWhiteSpace(list.Set.Title) ? list.Id : list.Set.Title.Trim(),
                        list.Set.Achievements.ToList())
                    {
                        ArtUrl = string.IsNullOrWhiteSpace(list.Set.GameArtUrl) ? null : list.Set.GameArtUrl.Trim()
                    })
                    .ToList()
            };

            if (fetched.Any(list => list.Set.EnglishRows != null))
            {
                // Each list in English where it has an English copy, as given where it is English.
                combined.EnglishRows = async token =>
                {
                    var rows = new List<AchievementDetail>();
                    foreach (var list in fetched)
                    {
                        rows.AddRange(list.Set.EnglishRows == null
                            ? list.Set.Achievements
                            : await list.Set.EnglishRows(token).ConfigureAwait(false) ?? new List<AchievementDetail>());
                    }

                    return rows;
                };
            }

            foreach (var art in fetched.SelectMany(list => list.Set.CategoryArtUrls))
            {
                if (!combined.CategoryArtUrls.ContainsKey(art.Key))
                {
                    combined.CategoryArtUrls[art.Key] = art.Value;
                }
            }

            // A list the source stored under a fuller id (TrueGaming's one page name) extends the link.
            var finalIds = fetched.Select(list => list.Id).ToList();
            if (!finalIds.SequenceEqual(requestedIds, StringComparer.OrdinalIgnoreCase))
            {
                combined.ExtendedLinkId = MetadataLinkIds.Join(finalIds);
                combined.RewritesUserLink = fetched
                    .Where(list => !string.IsNullOrWhiteSpace(list.Set.ExtendedLinkId))
                    .All(list => list.Set.RewritesUserLink);
            }

            return combined;
        }
    }
}
