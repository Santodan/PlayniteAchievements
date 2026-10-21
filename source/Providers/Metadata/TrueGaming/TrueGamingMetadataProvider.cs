using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.IO;
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
    /// or its edition variants there ("Fallout-4-PS4"), and where it led is kept in
    /// <see cref="TrueGamingPageStore"/>. Only the pages the wanted fields need are loaded; see
    /// <see cref="TrueGamingPageSet"/>. The sites are English only, so rows pair with the game's
    /// achievements by English title.
    /// </summary>
    internal sealed class TrueGamingMetadataProvider : IMetadataProvider, IMetadataLinkDetails
    {
        public const string MetadataKey = "TrueGaming";

        // Pages load in under 2 s; one still loading after this is read as it stands.
        private const int PageTimeoutMs = 8000;

        private readonly OffscreenViewLeaseSource _views;
        private readonly ILogger _logger;
        private readonly TrueGamingPageStore _store;
        private readonly Dictionary<string, TrueGamingPage> _pages =
            new Dictionary<string, TrueGamingPage>(StringComparer.OrdinalIgnoreCase);

        private IDisposable _lease;

        public TrueGamingMetadataProvider(IPlayniteAPI api, ILogger logger, string pluginUserDataPath)
        {
            _views = api == null ? null : new OffscreenViewLeaseSource(api, logger);
            _logger = logger;
            _store = new TrueGamingPageStore(
                string.IsNullOrWhiteSpace(pluginUserDataPath) ? null : Path.Combine(pluginUserDataPath, "metadata", "truegaming-pages.json"),
                logger);
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

            _store.Save();
        }

        /// <summary>
        /// The game's one page name, from the first page found by slugs built from its names.
        /// Only the pages the wanted fields need are looked for; what each site gave is kept.
        /// </summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_views == null || request == null)
            {
                return null;
            }

            var found = new List<TrueGamingFoundPage>();
            var outcomes = await AddNeededPagesAsync(request, found, new HashSet<TrueGamingSite>(), name: null, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': no TrueGaming page.");
                return null;
            }

            // A new lookup (a new game, or a reset link) starts the game's pages over.
            var linkId = TrueGamingLinks.BaseSlug(found[0].Slug);
            _store.Clear(GameId(request));
            Record(request, linkId, outcomes);
            _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {linkId} ({Describe(found)}).");
            return new MetadataSourceLink(linkId, request.Game?.Name, foundByName: true);
        }

        /// <summary>
        /// The pages the wanted fields need: those already known for the link's name, and the rest
        /// looked for under it or its edition variants on each site. A link in another form is
        /// stored back as its one name.
        /// </summary>
        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            var name = TrueGamingLinks.Normalize(linkId);
            if (_views == null || request == null || name == null)
            {
                return null;
            }

            var found = new List<TrueGamingFoundPage>();
            var failed = new HashSet<TrueGamingSite>();
            var outcomes = await AddNeededPagesAsync(request, found, failed, name, ct).ConfigureAwait(false);
            Record(request, name, outcomes);
            if (found.Count == 0)
            {
                return null;
            }

            found = found.OrderBy(page => TrueGamingSite.LookupOrder.ToList().IndexOf(page.Site)).ToList();
            var set = new AchievementSourceSet(Key, linkId, TrueGamingPageSet.Combine(found, request.BaseCategoryLabel));
            if (!string.Equals(name, linkId?.Trim(), StringComparison.Ordinal))
            {
                set.ExtendedLinkId = name;
            }

            // A needed page that did not load leaves the fields it shares with the others as
            // stored, not partly replaced; tags stay as stored too when no page flags any row.
            if (failed.Count > 0)
            {
                set.UnavailableFields |= MetadataField.Tags | MetadataField.Categories;
                _logger?.Debug(
                    $"[TrueGamingMetadata] '{request.Game?.Name}': {string.Join(", ", failed.Select(site => site.Host))} did not load; stored values kept.");
            }

            if (failed.Contains(TrueGamingSite.TrueAchievements))
            {
                set.UnavailableFields |= MetadataField.Points;
            }

            if (failed.Contains(TrueGamingSite.TrueTrophies))
            {
                set.UnavailableFields |= MetadataField.TrophyTypes;
            }

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
        /// fields still need: the page already known for <paramref name="name"/>, or one looked for
        /// under it (or, unlinked, under slugs built from the game's names). A site known to have
        /// no page is skipped. Returns what each look settled, to keep; a site that could not be
        /// read settles nothing and goes into <paramref name="failed"/>.
        /// </summary>
        private async Task<List<(TrueGamingSite Site, string PageSlug)>> AddNeededPagesAsync(
            MetadataLinkRequest request,
            List<TrueGamingFoundPage> found,
            ISet<TrueGamingSite> failed,
            string name,
            CancellationToken ct)
        {
            var outcomes = new List<(TrueGamingSite Site, string PageSlug)>();
            var wanted = request.WantedFields == MetadataField.None
                ? MetadataField.Tags | MetadataField.Categories
                : request.WantedFields;
            var targets = (request.Data?.Achievements ?? new List<AchievementDetail>())
                .Select(row => row?.DisplayName)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToList();
            var gameId = GameId(request);
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                if (!TrueGamingPageSet.NeedsPage(site, wanted, found.Select(page => page.Page), targets))
                {
                    continue;
                }

                if (name != null && _store.TryGet(gameId, name, site, out var known))
                {
                    if (known == null)
                    {
                        continue;
                    }

                    var page = await GetPageAsync(site, known, ct).ConfigureAwait(false);
                    if (page.Achievements.Count > 0)
                    {
                        found.Add(new TrueGamingFoundPage(site, known, page));
                    }
                    else
                    {
                        failed.Add(site);
                    }

                    continue;
                }

                var (hit, settled) = await FindPageAsync(site, request, targets, name, ct).ConfigureAwait(false);
                if (hit != null)
                {
                    found.Add(hit);
                    outcomes.Add((site, hit.Slug));
                }
                else if (settled)
                {
                    outcomes.Add((site, null));
                }
                else
                {
                    failed.Add(site);
                }
            }

            return outcomes;
        }

        /// <summary>
        /// The site's page for the linked <paramref name="name"/> (the name, then its edition
        /// variants), or else the first slug built from the game's names whose page is the same
        /// game, with edition variants only on the platform's own site. A page a user linked is
        /// taken as named; any other must share enough achievement names with the game. Settled
        /// is false when a slug could not be read, so a missing page is not concluded from it.
        /// </summary>
        private async Task<(TrueGamingFoundPage Page, bool Settled)> FindPageAsync(
            TrueGamingSite site,
            MetadataLinkRequest request,
            IReadOnlyCollection<string> targets,
            string name,
            CancellationToken ct)
        {
            var slugs = name != null
                ? TrueGamingLinks.SlugsFor(site, name)
                : TrueGamingLinks.CandidateSlugs(
                    request.SearchNames,
                    site == TrueGamingSite.For(request.ServicingProviderKey) ? site.Suffixes : Array.Empty<string>());
            var checkGame = name == null || !request.LinkIsUserSet;
            var settled = true;
            foreach (var candidate in slugs)
            {
                var page = await GetPageAsync(site, candidate, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0 && (!checkGame || TrueGamingPageSet.IsSameGame(page, targets)))
                {
                    return (new TrueGamingFoundPage(site, candidate, page), true);
                }

                settled &= page.Loaded;
            }

            return (null, settled);
        }

        /// <summary>The page each site gave the link ("TrueTrophies: Elden-Ring-PS5"), "None" where it has none.</summary>
        public string DescribeLink(Guid gameId, string linkId)
        {
            var name = TrueGamingLinks.Normalize(linkId);
            var pages = name == null ? Array.Empty<(TrueGamingSite Site, string PageSlug)>() : _store.GetPages(gameId, name);
            return pages.Count == 0
                ? null
                : string.Join(Environment.NewLine, pages.Select(page =>
                    $"{page.Site.Title}: {page.PageSlug ?? ResourceProvider.GetString("LOCNone")}"));
        }

        private void Record(MetadataLinkRequest request, string name, IEnumerable<(TrueGamingSite Site, string PageSlug)> outcomes)
        {
            var gameId = GameId(request);
            foreach (var (site, pageSlug) in outcomes)
            {
                _store.Set(gameId, name, site, pageSlug);
            }
        }

        private static Guid GameId(MetadataLinkRequest request) =>
            request.Data?.PlayniteGameId ?? request.Game?.Id ?? Guid.Empty;

        private static string Describe(IEnumerable<TrueGamingFoundPage> pages) =>
            string.Join(", ", pages.Select(page => page.Site.Host + "/game/" + page.Slug));

        /// <summary>
        /// A list page, loaded once per run; an empty page when it failed or does not exist, told
        /// apart by <see cref="TrueGamingPage.Loaded"/>.
        /// </summary>
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
                page.Loaded = !string.IsNullOrWhiteSpace(html) && !IsChallenge(html);
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

        private static bool IsChallenge(string html) =>
            html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("Attention Required", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
