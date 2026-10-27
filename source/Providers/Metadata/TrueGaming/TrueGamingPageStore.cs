using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Where each game's TrueGaming link leads on each site: the page found there, or that the site
    /// has none. Kept in the cache database by <see cref="MetadataSourcePageStore"/>, keyed by site
    /// prefix; rederived by refreshes, so it stays out of game data files. A game's record belongs
    /// to its link name, and a different name starts it again.
    /// </summary>
    internal sealed class TrueGamingPageStore
    {
        private readonly MetadataSourcePageStore _pages;

        public TrueGamingPageStore(MetadataSourcePageStore pages)
        {
            _pages = pages;
        }

        /// <summary>
        /// True when the site was looked at for <paramref name="name"/>; <paramref name="pageSlug"/>
        /// is then its page, or null for a site without one.
        /// </summary>
        public bool TryGet(Guid gameId, string name, TrueGamingSite site, out string pageSlug)
        {
            pageSlug = null;
            return _pages != null && _pages.TryGet(gameId, name, site.Prefix, out pageSlug);
        }

        /// <summary>Records the site's page for <paramref name="name"/>, null for none; a new name clears the game's other sites.</summary>
        public void Set(Guid gameId, string name, TrueGamingSite site, string pageSlug) =>
            _pages?.Set(gameId, name, site.Prefix, pageSlug);

        /// <summary>Forgets the sites of <paramref name="name"/>'s record other than <paramref name="sites"/>.</summary>
        public void Keep(Guid gameId, string name, IEnumerable<TrueGamingSite> sites) =>
            _pages?.Keep(gameId, name, (sites ?? Enumerable.Empty<TrueGamingSite>()).Select(site => site.Prefix));

        /// <summary>Each site looked at for <paramref name="name"/>, in lookup order, with its page or null for none.</summary>
        public IReadOnlyList<(TrueGamingSite Site, string PageSlug)> GetPages(Guid gameId, string name)
        {
            var known = (_pages?.GetPages(gameId, name) ?? Array.Empty<(string Site, string PageSlug)>())
                .ToDictionary(page => page.Site, page => page.PageSlug, StringComparer.OrdinalIgnoreCase);
            return TrueGamingSite.LookupOrder
                .Where(site => known.ContainsKey(site.Prefix))
                .Select(site => (site, known[site.Prefix]))
                .ToList();
        }

        /// <summary>Forgets the game's pages, so every site is looked at again.</summary>
        public void Clear(Guid gameId) => _pages?.Clear(gameId);
    }
}
