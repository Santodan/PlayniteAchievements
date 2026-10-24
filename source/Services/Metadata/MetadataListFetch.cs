using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
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
        public static async Task<AchievementSourceSet> FetchAsync(
            IMetadataProvider provider,
            MetadataLinkRequest request,
            string linkId,
            ILogger logger,
            CancellationToken ct)
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

            return Combine(provider.Key, linkId, parts, fetched);
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
            }

            return combined;
        }
    }
}
