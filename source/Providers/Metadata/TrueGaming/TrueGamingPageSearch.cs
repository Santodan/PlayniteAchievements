using PlayniteAchievements.Services.GameSearch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// One site's pages for a choice that site serves (TrueAchievements for Xbox points): each
    /// result is the page exactly as named, edition ending kept, so "BioShock-Xbox-One" is picked
    /// over the plain "BioShock". Only TrueAchievements' search lists games; on the other sites a
    /// page is picked by its address.
    /// </summary>
    internal sealed class TrueGamingPageSearch : IGameSearchSource
    {
        private readonly TrueGamingMetadataProvider _provider;
        private readonly TrueGamingSite _site;

        public TrueGamingPageSearch(TrueGamingMetadataProvider provider, TrueGamingSite site, string displayName)
        {
            _provider = provider;
            _site = site;
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public bool AllowsMultiple => false;

        public async Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            if (_site != TrueGamingSite.TrueAchievements)
            {
                return Array.Empty<GameSearchResult>();
            }

            var found = await _provider.SearchPagesAsync(query, ct).ConfigureAwait(false);
            if (found == null)
            {
                throw new GameSearchUnavailableException(DisplayName);
            }

            return found.Select(page => Result(page.Slug, page.Title)).ToList();
        }

        /// <summary>A page address on this site, or "game/Name"; a bare name is searched.</summary>
        public string TryParseId(string text)
        {
            var trimmed = text?.Trim() ?? string.Empty;
            return trimmed.IndexOf('/') >= 0 ? TrueGamingLinks.ExactSlug(_site, trimmed) : null;
        }

        /// <summary>The page's title from a search for its name, else its name with spaces; its edition's console as the platform.</summary>
        public async Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct)
        {
            var title = TrueGamingLinks.BaseSlug(id)?.Replace('-', ' ') ?? id;
            if (_site == TrueGamingSite.TrueAchievements)
            {
                var found = await _provider.SearchPagesAsync(id.Replace('-', ' '), ct).ConfigureAwait(false);
                var match = (found ?? Array.Empty<(string Slug, string Title)>())
                    .FirstOrDefault(page => string.Equals(page.Slug, id, StringComparison.OrdinalIgnoreCase));
                title = match.Title ?? title;
            }

            return Result(id, title);
        }

        private GameSearchResult Result(string slug, string title) =>
            new GameSearchResult(slug, title, platforms: _site.PlatformOf(slug));
    }
}
