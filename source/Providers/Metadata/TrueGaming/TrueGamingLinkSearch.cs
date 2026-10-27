using PlayniteAchievements.Services.GameSearch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Search of all three TrueGaming sites for a TrueGaming link: each result is one site's page
    /// of one edition, and the picked page is kept with its site ("tt:Elden-Ring-PS4") and read
    /// first. A collection names one per game.
    /// </summary>
    internal sealed class TrueGamingLinkSearch : IGameSearchSource
    {
        private readonly TrueGamingMetadataProvider _provider;

        public TrueGamingLinkSearch(TrueGamingMetadataProvider provider, string displayName)
        {
            _provider = provider;
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public bool AllowsMultiple => true;

        public async Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            var found = await _provider.SearchPagesAsync(query, ct).ConfigureAwait(false);
            if (found == null)
            {
                throw new GameSearchUnavailableException(DisplayName);
            }

            return found.Select(ToResult).ToList();
        }

        /// <summary>
        /// A page address on any of the three sites, its site and edition kept. A bare name is
        /// searched rather than read as a page name, since a game's own name is often one ("Half-Life").
        /// </summary>
        public string TryParseId(string text) => TrueGamingLinks.PickedIdFromAddress(text);

        /// <summary>The page's title and platforms from its site's search for its name; the page itself when the search does not list it.</summary>
        public async Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct)
        {
            var picked = TrueGamingLinks.Picked(id);
            if (picked.Site == null)
            {
                return new GameSearchResult(id, id);
            }

            var found = await _provider.SearchPagesAsync(TrueGamingLinks.BaseSlug(picked.Slug).Replace('-', ' '), ct).ConfigureAwait(false);
            var match = (found ?? Array.Empty<(TrueGamingSite Site, string Slug, string Title, string Platforms)>())
                .FirstOrDefault(page => page.Site == picked.Site && string.Equals(page.Slug, picked.Slug, StringComparison.OrdinalIgnoreCase));
            return match.Site != null
                ? ToResult(match)
                : new GameSearchResult(id, picked.Slug, platforms: picked.Site.Title);
        }

        /// <summary>A page as a result: its title, with its site and platforms ("TrueTrophies · PlayStation 4").</summary>
        private static GameSearchResult ToResult((TrueGamingSite Site, string Slug, string Title, string Platforms) page) =>
            new GameSearchResult(
                TrueGamingLinks.PickedId(page.Site, page.Slug),
                page.Title,
                platforms: string.IsNullOrWhiteSpace(page.Platforms) ? page.Site.Title : $"{page.Site.Title} · {page.Platforms}");
    }
}
