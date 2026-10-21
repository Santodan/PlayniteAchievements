using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Achievement flags (as tags), DLC groups (as categories), gamerscore and trophy ranks from
    /// the TrueGaming sites: TrueAchievements, TrueTrophies and TrueSteamAchievements, tried in that
    /// order for every platform. Pages are public and load through an offscreen browser view. A
    /// game links to one page name ("Fallout-4"), built from its name; each site is read under it
    /// or its edition variants there ("Fallout-4-PS4"). Only the pages the
    /// wanted fields need are loaded; see <see cref="TrueGamingPageSet"/>. The sites are English
    /// only, so rows pair with the game's achievements by English title.
    /// </summary>
    internal sealed class TrueGamingMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "TrueGaming";

        // Pages load in under 2 s; one still loading after this is read as it stands.
        private const int PageTimeoutMs = 8000;

        private readonly OffscreenViewLeaseSource _views;
        private readonly ILogger _logger;
        private readonly Dictionary<string, TrueGamingPage> _pages =
            new Dictionary<string, TrueGamingPage>(StringComparer.OrdinalIgnoreCase);

        // Sites with no page for a game, found while extending an automatic link; not looked up
        // again this session. A reset link is resolved afresh, without them.
        private readonly ConcurrentDictionary<string, bool> _misses =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private IDisposable _lease;

        public TrueGamingMetadataProvider(IPlayniteAPI api, ILogger logger)
        {
            _views = api == null ? null : new OffscreenViewLeaseSource(api, logger);
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_TrueGaming";

        public string LinkPlaceholder => "Game-Name";

        public MetadataField SupportedFields =>
            MetadataField.Tags | MetadataField.Categories | MetadataField.CategoryArt | MetadataField.Points | MetadataField.TrophyTypes;

        public bool IsNativeFor(string servicingProviderKey) => false;

        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            if (_views == null)
            {
                return Task.FromResult(false);
            }

            _lease = _views.BeginLease();
            return Task.FromResult(true);
        }

        public void EndRun()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            lock (_pages)
            {
                _pages.Clear();
            }
        }

        /// <summary>
        /// The game's one page name, from the first page found by slugs built from its names.
        /// Only the pages the wanted fields need are looked for.
        /// </summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_views == null || request == null)
            {
                return null;
            }

            var found = new List<TrueGamingFoundPage>();
            await AddNeededPagesAsync(request, found, slug: null, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': no TrueGaming page.");
                return null;
            }

            var linkId = TrueGamingLinks.BaseSlug(found[0].Slug);
            _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {linkId} ({string.Join(", ", found.Select(page => page.Site.Host + "/game/" + page.Slug))}).");
            return new MetadataSourceLink(linkId, request.Game?.Name, foundByName: true);
        }

        /// <summary>
        /// Each page the wanted fields need, under the link's one name or its edition variants on
        /// that site, read together. A link in another form is stored back as its one name.
        /// </summary>
        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            var slug = TrueGamingLinks.Normalize(linkId);
            if (_views == null || request == null || slug == null)
            {
                return null;
            }

            var found = new List<TrueGamingFoundPage>();
            await AddNeededPagesAsync(request, found, slug, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                return null;
            }

            found = found.OrderBy(page => TrueGamingSite.LookupOrder.ToList().IndexOf(page.Site)).ToList();
            var set = new AchievementSourceSet(Key, linkId, TrueGamingPageSet.Combine(found, request.BaseCategoryLabel));
            if (!string.Equals(slug, linkId?.Trim(), StringComparison.Ordinal))
            {
                set.ExtendedLinkId = slug;
            }

            // Tags stay as stored when no page flags any row.
            if (set.Achievements.All(row => row.Tags == null))
            {
                set.UnavailableFields |= MetadataField.Tags;
            }

            foreach (var art in found.SelectMany(page => page.Page.DlcArt))
            {
                var label = CategoryPathHelper.SanitizeSegment(art.Key);
                if (!string.IsNullOrWhiteSpace(label) && !set.CategoryArtUrls.ContainsKey(label))
                {
                    set.CategoryArtUrls[label] = art.Value;
                }
            }

            return set;
        }

        /// <summary>
        /// Adds to <paramref name="found"/>, site by site in lookup order, each page the wanted
        /// fields still need: under <paramref name="slug"/> when the game is linked, else by slugs
        /// built from its names. A site without the linked page is not asked again this session.
        /// </summary>
        private async Task AddNeededPagesAsync(
            MetadataLinkRequest request,
            List<TrueGamingFoundPage> found,
            string slug,
            CancellationToken ct)
        {
            var wanted = request.WantedFields == MetadataField.None
                ? MetadataField.Tags | MetadataField.Categories
                : request.WantedFields;
            var targets = (request.Data?.Achievements ?? new List<AchievementDetail>())
                .Select(row => row?.DisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                if (!TrueGamingPageSet.NeedsPage(site, wanted, found.Select(page => page.Page), targets))
                {
                    continue;
                }

                var missKey = $"{request.Game?.Id}|{site.Name}|{slug}";
                if (slug != null && _misses.ContainsKey(missKey))
                {
                    continue;
                }

                var page = await FindPageAsync(site, request, targets, slug, ct).ConfigureAwait(false);
                if (page != null)
                {
                    found.Add(page);
                }
                else if (slug != null)
                {
                    _misses[missKey] = true;
                }
            }
        }

        /// <summary>
        /// The site's page for the linked <paramref name="slug"/> (the name, then its edition
        /// variants), or else the first slug built from the game's names whose page is the same
        /// game, with edition variants only on the platform's own site. A page a user linked is
        /// taken as named; any other must share enough achievement names with the game.
        /// </summary>
        private async Task<TrueGamingFoundPage> FindPageAsync(
            TrueGamingSite site,
            MetadataLinkRequest request,
            IReadOnlyCollection<string> targets,
            string slug,
            CancellationToken ct)
        {
            var slugs = slug != null
                ? TrueGamingLinks.SlugsFor(site, slug)
                : TrueGamingLinks.CandidateSlugs(
                    request.SearchNames,
                    site == TrueGamingSite.For(request.ServicingProviderKey) ? site.Suffixes : Array.Empty<string>());
            var checkGame = slug == null || !request.LinkIsUserSet;
            foreach (var candidate in slugs)
            {
                var page = await GetPageAsync(site, candidate, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0 && (!checkGame || TrueGamingPageSet.IsSameGame(page, targets)))
                {
                    return new TrueGamingFoundPage(site, candidate, page);
                }
            }

            return null;
        }
        /// <summary>A list page, loaded once per run; an empty page when it failed or does not exist.</summary>
        private async Task<TrueGamingPage> GetPageAsync(TrueGamingSite site, string slug, CancellationToken ct)
        {
            var url = site.ListUrl(slug);
            lock (_pages)
            {
                if (_pages.TryGetValue(url, out var cached))
                {
                    return cached;
                }
            }

            TrueGamingPage page;
            try
            {
                // A page still loading at the timeout (ads, trackers) usually has its list already,
                // so it is read as it stands. Its view is replaced, so the page's late load cannot
                // end the next page's wait.
                var (html, timedOut) = await _views.WithDiscardableViewAsync(async view =>
                {
                    var late = false;
                    try
                    {
                        await view.NavigateAndWaitAsync(url, PageTimeoutMs).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        late = true;
                    }

                    var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                    return ((source, late), late);
                }, ct).ConfigureAwait(false);
                page = TrueGamingPageParser.Parse(html);
                if (timedOut)
                {
                    _logger?.Debug($"[TrueGamingMetadata] {url} still loading after {PageTimeoutMs} ms; read with {page.Achievements.Count} rows.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[TrueGamingMetadata] Loading {url} failed: {ex.GetType().Name}: {ex.Message}");
                page = new TrueGamingPage();
            }

            lock (_pages)
            {
                _pages[url] = page;
            }

            return page;
        }
    }
}
