using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>
    /// The lists a metadata link names. A collection is one game here but several games on a
    /// source, so its link holds each list's id, separated by commas ("mass-effect, mass-effect-2").
    /// Providers read one list at a time; the overlay fetches each and joins them.
    /// </summary>
    internal static class MetadataLinkIds
    {
        public const char Separator = ',';

        /// <summary>The link's list ids in order, trimmed, without blanks or repeats.</summary>
        public static IReadOnlyList<string> Parse(string linkId)
        {
            return (linkId ?? string.Empty)
                .Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>One link from list ids, in order, without blanks or repeats; null when none is left.</summary>
        public static string Join(IEnumerable<string> ids)
        {
            var parts = Parse(string.Join(Separator.ToString(), (ids ?? Enumerable.Empty<string>()).Where(id => id != null)));
            return parts.Count == 0 ? null : string.Join(Separator + " ", parts);
        }

        /// <summary>True when <paramref name="linkId"/> names every list <paramref name="earlier"/> named, in any order.</summary>
        public static bool ContainsAll(string linkId, string earlier)
        {
            var before = Parse(earlier);
            if (before.Count == 0)
            {
                return false;
            }

            var now = new HashSet<string>(Parse(linkId), StringComparer.OrdinalIgnoreCase);
            return before.All(now.Contains);
        }

        /// <summary>True when two links name the same lists, in any order.</summary>
        public static bool SameIds(string linkId, string other) =>
            ContainsAll(linkId, other) && ContainsAll(other, linkId);

        /// <summary>The link as the user enters it: each list as its source shows it.</summary>
        public static string ShownLink(IMetadataLinkDetails details, string linkId)
        {
            if (details == null || linkId == null)
            {
                return null;
            }

            var parts = Parse(linkId);
            return parts.Count > 1 ? Join(parts.Select(part => details.ShownLink(part) ?? part)) : details.ShownLink(One(parts, linkId));
        }

        // The one list of a link that names one, however it was written ("a", "a,", "a, a").
        private static string One(IReadOnlyList<string> parts, string linkId) => parts.Count == 1 ? parts[0] : linkId;

        /// <summary>What each list of the link led to, one block per list.</summary>
        public static string DescribeLink(IMetadataLinkDetails details, Guid gameId, string linkId)
        {
            if (details == null || linkId == null)
            {
                return null;
            }

            var parts = Parse(linkId);
            if (parts.Count <= 1)
            {
                return details.DescribeLink(gameId, One(parts, linkId));
            }

            var described = parts
                .Select(part => details.DescribeLink(gameId, part))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
            return described.Count == 0 ? null : string.Join(Environment.NewLine, described);
        }

        /// <summary>Each list's pages, in the link's order.</summary>
        public static IReadOnlyList<MetadataPageLink> PageLinks(IMetadataPageLinks pages, Guid gameId, string linkId, string servicingProviderKey)
        {
            if (pages == null || linkId == null)
            {
                return Array.Empty<MetadataPageLink>();
            }

            var parts = Parse(linkId);
            return parts.Count <= 1
                ? pages.PageLinks(gameId, One(parts, linkId), servicingProviderKey) ?? Array.Empty<MetadataPageLink>()
                : parts.SelectMany(part => pages.PageLinks(gameId, part, servicingProviderKey) ?? Array.Empty<MetadataPageLink>()).ToList();
        }
    }
}
