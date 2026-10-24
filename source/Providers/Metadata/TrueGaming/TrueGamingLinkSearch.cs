using PlayniteAchievements.Services.GameSearch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// TrueAchievements search for a TrueGaming link: one page name stands for the game on all
    /// three sites; a collection names one per game.
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

            return found.Select(page => new GameSearchResult(page.Slug, page.Title)).ToList();
        }

        /// <summary>
        /// A page address on any of the three sites, or "game/Name". A bare name is searched rather
        /// than read as a page name, since a game's own name is often one ("Half-Life").
        /// </summary>
        public string TryParseId(string text)
        {
            var trimmed = text?.Trim() ?? string.Empty;
            return trimmed.IndexOf('/') >= 0 ? TrueGamingLinks.Normalize(trimmed) : null;
        }

        /// <summary>The page's title from a search for its name; the page name itself when the search does not list it.</summary>
        public async Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct)
        {
            var found = await _provider.SearchPagesAsync(id.Replace('-', ' '), ct).ConfigureAwait(false);
            var match = (found ?? Array.Empty<(string Slug, string Title)>())
                .FirstOrDefault(page => string.Equals(page.Slug, id, StringComparison.OrdinalIgnoreCase));
            return new GameSearchResult(id, match.Title ?? id);
        }
    }
}
