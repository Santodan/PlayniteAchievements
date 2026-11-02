using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Achievement flags, as tags, from the TrueGaming sites: TrueAchievements, TrueTrophies and
    /// TrueSteamAchievements, tried in that order for every platform. Pages are public and load through an offscreen browser view. A
    /// game links to one site's page ("tt:Fallout-4-PS4"), read first; another site is read under
    /// the page's name or its edition variants there ("Fallout-4"), and where it led is kept in
    /// <see cref="TrueGamingPageStore"/>. Only the pages the wanted fields need are loaded; see
    /// <see cref="TrueGamingPageSet"/>. The sites are English only, so rows pair with the game's
    /// achievements by English title.
    /// </summary>
    internal sealed class TrueGamingMetadataProvider : IMetadataProvider, IMetadataLinkDetails, IMetadataPageLinks, IMetadataLinkSearch, IMetadataLinkPlatform
    {
        public const string MetadataKey = "TrueGaming";

        // Pages load in under 2 s; one still loading after this is read as it stands.
        private const int PageTimeoutMs = 8000;

        private readonly OffscreenViewLeaseSource _views;
        private readonly ILogger _logger;
        private readonly TrueGamingPageStore _store;
        // Pages are shared while a game is looked up and fetched, so only the recent ones are kept.
        private readonly LruCache<string, TrueGamingPage> _pages =
            new LruCache<string, TrueGamingPage>(64, StringComparer.OrdinalIgnoreCase);

        private IDisposable _lease;

        public TrueGamingMetadataProvider(IPlayniteAPI api, ILogger logger, MetadataStateStore state)
        {
            _views = api == null ? null : new OffscreenViewLeaseSource(api, logger);
            _logger = logger;
            _store = new TrueGamingPageStore(state?.Pages(MetadataKey));
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_TrueGaming";

        public IGameSearchSource CreateLinkSearch(Guid gameId, string servicingProviderKey) =>
            _views == null ? null : new TrueGamingLinkSearch(this, ResourceProvider.GetString(DisplayNameKey));

        public MetadataField SupportedFields =>
            MetadataField.Tags;

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

            lock (_searches)
            {
                _searches.Clear();
            }

            lock (_challenged)
            {
                _challenged.Clear();
            }
        }

        // Sites still answering with a challenge page after the wait; not loaded again this run.
        private readonly HashSet<TrueGamingSite> _challenged = new HashSet<TrueGamingSite>();

        private bool IsChallenged(TrueGamingSite site)
        {
            lock (_challenged)
            {
                return _challenged.Contains(site);
            }
        }

        private void MarkChallenged(TrueGamingSite site)
        {
            bool added;
            lock (_challenged)
            {
                added = _challenged.Add(site);
            }

            if (added)
            {
                _logger?.Debug($"[TrueGamingMetadata] {site.Host} is answering with a challenge page; skipping it for the rest of this refresh.");
            }
        }

        /// <summary>
        /// The game's link: the page name its achievements match best, as that name's first page in
        /// lookup order ("ta:Satisfactory"), the form the link search lists. Without achievements,
        /// the first page found by slugs built from its names. What each site gave is kept.
        /// </summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_views == null || request == null)
            {
                return null;
            }

            // With the game's achievements at hand, each page name the slug and TrueAchievements
            // search offer is judged by how many of them its pages match, and a collection is also
            // looked for game by game.
            var targets = request.Data?.Achievements?.Where(row => row != null).ToList();
            if (targets?.Count > 0)
            {
                var health = new MetadataLookupHealth();
                var own = TrueGamingSite.For(request.ServicingProviderKey);
                var search = new MetadataPageSearch(
                    (name, token) => CandidatesAsync(name, own, health, token),
                    (slug, token) => RowsAsync(request, slug, health, token),
                    (name, token) => CandidatesAsync(name, own, health, token, buildSlug: false));
                var names = request.SearchNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var choice = await search.ChooseAsync(names, targets, MetadataSubGame.From(targets), ct, request.CategoryNames, sharedNames: request.AlternateNames, englishTitles: request.EnglishTitles).ConfigureAwait(false);
                if (health.LeavesDoubt(choice))
                {
                    _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': {choice?.Matched ?? 0}/{targets.Count} matched, but some pages could not be read; looking again next refresh.");
                    return MetadataSourceLink.Unavailable(choice?.Trace);
                }

                if (choice == null)
                {
                    _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': no TrueGaming page matches its achievements.");
                    return null;
                }

                // Each chosen page name is linked as its first page in lookup order, the form the
                // link search lists ("ta:Satisfactory"); the pages judging it are already loaded.
                var targetNames = targets.Select(row => row.DisplayName).Where(title => !string.IsNullOrWhiteSpace(title)).ToList();
                var pages = new List<string>();
                foreach (var id in choice.Ids.Select(TrueGamingLinks.BaseSlug))
                {
                    var (page, settled) = await FirstPageAsync(request, targetNames, id, ct).ConfigureAwait(false);
                    if (!settled)
                    {
                        _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': {id} matched, but some pages could not be read; looking again next refresh.");
                        return MetadataSourceLink.Unavailable(choice.Trace);
                    }

                    pages.Add(page == null ? id : TrueGamingLinks.PickedId(page.Site, page.Slug));
                }

                // A new lookup (a new game, or a reset link) starts the game's pages over.
                _store.Clear(GameId(request));
                var chosen = MetadataLinkIds.Join(pages);
                _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {chosen} ({choice.Matched}/{targets.Count} matched; {choice.Trace}).");
                return new MetadataSourceLink(chosen, choice.Label ?? request.Game?.Name, foundByName: true) { Trace = choice.Trace, Matched = choice.Matched };
            }

            var found = new List<TrueGamingFoundPage>();
            var unread = new HashSet<TrueGamingSite>();
            var outcomes = await AddNeededPagesAsync(request, found, unread, name: null, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': no TrueGaming page{(unread.Count > 0 ? ", but some pages could not be read" : string.Empty)}.");
                return unread.Count > 0 ? MetadataSourceLink.Unavailable() : null;
            }

            // A new lookup (a new game, or a reset link) starts the game's pages over.
            var linkId = TrueGamingLinks.PickedId(found[0].Site, found[0].Slug);
            _store.Clear(GameId(request));
            Record(request, StoreName(linkId), outcomes);
            _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {linkId} ({Describe(found)}).");
            return new MetadataSourceLink(linkId, request.Game?.Name, foundByName: true);
        }

        // TrueAchievements search results judged per name; each can cost a page load on every site.
        private const int SearchCandidatesJudged = 3;

        private readonly Dictionary<string, IReadOnlyList<(string Slug, string Title)>> _searches =
            new Dictionary<string, IReadOnlyList<(string Slug, string Title)>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The page names to judge for one name: its own slug, then what the game's own site's
        /// search lists, then what TrueAchievements' search lists, each once. A broader form of a
        /// name ("Spyro Year of the") is searched only: a slug built from it seldom names a page
        /// and costs a load on every site.
        /// </summary>
        private async Task<IReadOnlyList<MetadataPageCandidate>> CandidatesAsync(string name, TrueGamingSite own, MetadataLookupHealth health, CancellationToken ct, bool buildSlug = true)
        {
            var candidates = new List<MetadataPageCandidate>();
            var slug = buildSlug ? TrueGamingLinks.ToSlug(name) : null;
            if (slug != null)
            {
                candidates.Add(new MetadataPageCandidate(slug, name, "built name"));
            }

            foreach (var site in new[] { own, TrueGamingSite.TrueAchievements }.Distinct())
            {
                var results = await SearchAsync(site, name, health, ct).ConfigureAwait(false);
                for (var i = 0; i < results.Count; i++)
                {
                    var page = TrueGamingLinks.BaseSlug(results[i].Slug);
                    if (candidates.All(candidate => !string.Equals(candidate.Id, page, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(new MetadataPageCandidate(page, results[i].Title, $"{site.Title} search #{i + 1}"));
                    }
                }
            }

            return candidates;
        }

        /// <summary>One site's search results for a name, asked once per run; a search that did not answer is not kept.</summary>
        private async Task<IReadOnlyList<(string Slug, string Title)>> SearchAsync(TrueGamingSite site, string name, MetadataLookupHealth health, CancellationToken ct)
        {
            var url = TrueGamingLinks.SearchUrl(site, name);
            lock (_searches)
            {
                if (_searches.TryGetValue(url, out var cached))
                {
                    return cached;
                }
            }

            if (IsChallenged(site))
            {
                health.MarkUnreadable();
                return Array.Empty<(string, string)>();
            }

            // The search page is markup; the API answers as a text document.
            var text = await ReadSearchPageAsync(url, ct, site, asText: site != TrueGamingSite.TrueSteamAchievements).ConfigureAwait(false);
            if (text == null)
            {
                health.MarkUnreadable();
                return Array.Empty<(string, string)>();
            }

            var results = TrueGamingLinks.ParseSiteSearch(text).Take(SearchCandidatesJudged).Select(game => (game.Slug, game.Title)).ToList();
            lock (_searches)
            {
                _searches[url] = results;
            }

            return results;
        }

        /// <summary>
        /// Every page each site's search answers for a name typed into the link picker, site by
        /// site in lookup order and each in the site's order, with its platforms. Not kept. Null
        /// when no site's search could be read.
        /// </summary>
        internal async Task<IReadOnlyList<(TrueGamingSite Site, string Slug, string Title, string Platforms)>> SearchPagesAsync(string name, CancellationToken ct)
        {
            if (_views == null || string.IsNullOrWhiteSpace(name))
            {
                return _views == null ? null : Array.Empty<(TrueGamingSite, string, string, string)>();
            }

            var pages = new List<(TrueGamingSite Site, string Slug, string Title, string Platforms)>();
            var read = false;
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                // The search page is markup; the API answers as a text document.
                var asText = site != TrueGamingSite.TrueSteamAchievements;
                var text = await ReadSearchPageAsync(TrueGamingLinks.SearchUrl(site, name.Trim()), ct, site, asText).ConfigureAwait(false);
                if (text == null)
                {
                    continue;
                }

                read = true;
                pages.AddRange(TrueGamingLinks.ParseSiteSearch(text).Select(game => (site, game.Slug, game.Title, game.Platforms)));
            }

            return read ? pages : null;
        }

        /// <summary>A search page's text, or null when it could not be read or was a challenge page.</summary>
        private async Task<string> ReadSearchPageAsync(string url, CancellationToken ct, TrueGamingSite site = null, bool asText = true)
        {
            site = site ?? TrueGamingSite.TrueAchievements;
            if (IsChallenged(site))
            {
                return null;
            }

            string text;
            try
            {
                text = await _views.WithNavigableViewAsync(async view =>
                {
                    await view.NavigateAndWaitAsync(url, PageTimeoutMs).ConfigureAwait(false);
                    var source = await ReadPastChallengeAsync(view.GetPageSourceAsync, ct).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(source) || IsChallenge(source) || !asText
                        ? source
                        : await view.GetPageTextAsync().ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[TrueGamingMetadata] Loading {url} failed: {ex.GetType().Name}: {ex.Message}");
                text = null;
            }

            if (string.IsNullOrWhiteSpace(text) || IsChallenge(text))
            {
                _logger?.Debug($"[TrueGamingMetadata] {url} could not be read{(string.IsNullOrWhiteSpace(text) ? string.Empty : " (challenge page)")}.");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    MarkChallenged(site);
                }

                return null;
            }

            return text;
        }

        /// <summary>
        /// The rows a page name gives, site by site, the platform's own site first: one page when
        /// it matches every achievement, else the sites' pages together, as a fetch reads them.
        /// Null when no site has it.
        /// </summary>
        private async Task<IList<AchievementDetail>> RowsAsync(MetadataLinkRequest request, string slug, MetadataLookupHealth health, CancellationToken ct)
        {
            var achievements = (request.Data?.Achievements ?? new List<AchievementDetail>()).Where(row => row != null).ToList();
            var targets = achievements
                .Select(row => row.DisplayName)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToList();
            var own = TrueGamingSite.For(request.ServicingProviderKey);
            var found = new List<TrueGamingFoundPage>();
            List<AchievementDetail> rows = null;
            foreach (var site in new[] { own }.Concat(TrueGamingSite.LookupOrder.Where(site => site != own)))
            {
                var (hit, settled) = await FindPageAsync(site, request, targets, slug, ct).ConfigureAwait(false);
                if (hit == null)
                {
                    if (!settled)
                    {
                        health.MarkUnreadable();
                    }

                    continue;
                }

                found.Add(hit);
                rows = TrueGamingPageSet.Combine(found.OrderBy(page => TrueGamingSite.LookupOrder.ToList().IndexOf(page.Site)).ToList(), request.BaseCategoryLabel);
                if (CrossSourceAchievementMatcher.Match(achievements, rows).Count >= achievements.Count)
                {
                    break;
                }
            }

            return rows;
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

            var picked = TrueGamingLinks.Picked(linkId);
            var found = new List<TrueGamingFoundPage>();
            var failed = new HashSet<TrueGamingSite>();
            var outcomes = await AddNeededPagesAsync(request, found, failed, name, ct, picked).ConfigureAwait(false);
            Record(request, StoreName(linkId), outcomes);

            // Sites this refresh did not need are forgotten, so the link lists only the pages its
            // tags came from; a site that did not load leaves the record as it was.
            if (failed.Count == 0)
            {
                _store.Keep(GameId(request), StoreName(linkId), outcomes.Select(outcome => outcome.Site));
            }
            if (found.Count == 0)
            {
                return null;
            }

            var order = SiteOrder(picked.Site);
            found = found.OrderBy(page => order.IndexOf(page.Site)).ToList();
            var combined = TrueGamingPageSet.CombineAll(found, request.BaseCategoryLabel, TrueGamingSite.For(request.ServicingProviderKey));
            var set = new AchievementSourceSet(Key, linkId, combined.Rows)
            {
                ApiNameAliases = combined.Aliases
            };
            if (failed.Count > 0 || combined.MergedByDescription > 0 || combined.MergedByPosition > 0)
            {
                _logger?.Debug(
                    $"[TrueGamingMetadata] '{request.Game?.Name}' ({name}): " +
                    $"{string.Join(", ", found.Select(page => $"{page.Site.Host}/game/{page.Slug} {page.Page.Achievements.Count} rows"))}; " +
                    $"{combined.Rows.Count} combined, {combined.MergedByDescription} joined by description, {combined.MergedByPosition} by position" +
                    $"{(failed.Count > 0 ? $"; not loaded: {string.Join(", ", failed.Select(site => site.Host))}" : string.Empty)}.");
            }
            // A found link stored as a page name before links named their site is stored as the
            // page read first ("ta:Satisfactory"), which reads the same pages. A site that did not
            // load may have been the first, so the link is then only brought to its one name.
            if (picked.Site == null)
            {
                if (failed.Count == 0)
                {
                    set.ExtendedLinkId = TrueGamingLinks.PickedId(found[0].Site, found[0].Slug);
                }
                else if (!string.Equals(name, linkId?.Trim(), StringComparison.Ordinal))
                {
                    set.ExtendedLinkId = name;
                }
            }

            // A needed page that did not load leaves the tags as stored, not partly replaced; so
            // does a set where no page flags any row.
            if (failed.Count > 0)
            {
                set.UnavailableFields |= MetadataField.Tags;
                _logger?.Debug(
                    $"[TrueGamingMetadata] '{request.Game?.Name}': {string.Join(", ", failed.Select(site => site.Host))} did not load; stored values kept.");
            }

            if (set.Achievements.All(row => row.Tags == null))
            {
                set.UnavailableFields |= MetadataField.Tags;
            }

            return set;
        }

        /// <summary>The sites in the order a link reads them: a picked page's site first, then lookup order.</summary>
        private static List<TrueGamingSite> SiteOrder(TrueGamingSite pickedSite) =>
            (pickedSite == null ? Enumerable.Empty<TrueGamingSite>() : new[] { pickedSite })
                .Concat(TrueGamingSite.LookupOrder.Where(site => site != pickedSite))
                .ToList();

        /// <summary>What a link's pages are kept under: a picked page's link as it is, a page name without its edition.</summary>
        private static string StoreName(string linkId) =>
            TrueGamingLinks.Picked(linkId).Site != null ? linkId.Trim() : TrueGamingLinks.Normalize(linkId);

        /// <summary>
        /// Adds to <paramref name="found"/>, site by site (a <paramref name="picked"/> page's site
        /// first, then lookup order), each page the wanted fields still need: the picked page as
        /// named, the page already known for <paramref name="name"/>, or one looked for under it
        /// (or, unlinked, under slugs built from the game's names). A site known to have no page is
        /// skipped. Returns what each look settled, to keep; a site that could not be read settles
        /// nothing and goes into <paramref name="failed"/>.
        /// </summary>
        private async Task<List<(TrueGamingSite Site, string PageSlug)>> AddNeededPagesAsync(
            MetadataLinkRequest request,
            List<TrueGamingFoundPage> found,
            ISet<TrueGamingSite> failed,
            string name,
            CancellationToken ct,
            (TrueGamingSite Site, string Slug) picked = default)
        {
            var outcomes = new List<(TrueGamingSite Site, string PageSlug)>();
            var wanted = request.WantedFields == MetadataField.None
                ? MetadataField.Tags
                : request.WantedFields;
            var targets = (request.Data?.Achievements ?? new List<AchievementDetail>())
                .Select(row => row?.DisplayName)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToList();
            var gameId = GameId(request);
            var storeName = picked.Site == null ? name : TrueGamingLinks.PickedId(picked.Site, picked.Slug);
            foreach (var site in SiteOrder(picked.Site))
            {
                if (!TrueGamingPageSet.NeedsPage(site, wanted, found.Select(page => page.Page), targets))
                {
                    continue;
                }

                if (site == picked.Site)
                {
                    var page = await GetPageAsync(site, picked.Slug, ct).ConfigureAwait(false);
                    if (page.Achievements.Count > 0)
                    {
                        found.Add(new TrueGamingFoundPage(site, picked.Slug, page));
                        outcomes.Add((site, picked.Slug));
                    }
                    else if (page.Settles)
                    {
                        outcomes.Add((site, null));
                    }
                    else
                    {
                        failed.Add(site);
                    }

                    continue;
                }

                if (name != null && _store.TryGet(gameId, storeName, site, out var known))
                {
                    if (known == null)
                    {
                        outcomes.Add((site, null));
                        continue;
                    }

                    var page = await GetPageAsync(site, known, ct).ConfigureAwait(false);
                    if (page.Achievements.Count > 0)
                    {
                        found.Add(new TrueGamingFoundPage(site, known, page));
                        outcomes.Add((site, known));
                        continue;
                    }

                    if (!page.Settles)
                    {
                        failed.Add(site);
                        continue;
                    }

                    // The stored page now lists nothing (the site moved or renamed it), so the
                    // site is looked at again as if nothing were stored.
                    _logger?.Debug($"[TrueGamingMetadata] {site.ListUrl(known)} lists no achievements now; looking for the page again.");
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
            var own = TrueGamingSite.For(request.ServicingProviderKey);
            var slugs = name != null
                ? TrueGamingLinks.SlugsToTry(site, own, name, request.LinkIsUserSet)
                : TrueGamingLinks.CandidateSlugs(request.SearchNames, site == own ? site.Suffixes : Array.Empty<string>());
            var checkGame = name == null || !request.LinkIsUserSet;
            var settled = true;
            foreach (var candidate in slugs)
            {
                var page = await GetPageAsync(site, candidate, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0 &&
                    (!checkGame || TrueGamingPageSet.IsSameGame(page, targets) ||
                     TrueGamingPageSet.IsSameGame(page, await EnglishTitlesAsync(request, ct).ConfigureAwait(false))))
                {
                    return (new TrueGamingFoundPage(site, candidate, page), true);
                }

                settled &= page.Settles;
            }

            return (null, settled);
        }

        /// <summary>
        /// The first site's page for <paramref name="name"/> in lookup order, as a fetch would read
        /// it; null when no site has it. Not settled when a site before it could not be read.
        /// </summary>
        private async Task<(TrueGamingFoundPage Page, bool Settled)> FirstPageAsync(
            MetadataLinkRequest request,
            IReadOnlyCollection<string> targetNames,
            string name,
            CancellationToken ct)
        {
            foreach (var site in TrueGamingSite.LookupOrder)
            {
                var (page, settled) = await FindPageAsync(site, request, targetNames, name, ct).ConfigureAwait(false);
                if (page != null || !settled)
                {
                    return (page, settled);
                }
            }

            return (null, true);
        }

        /// <summary>One site's list page, as a fetch reads it; for the developer probes.</summary>
        internal Task<TrueGamingPage> LoadPageAsync(TrueGamingSite site, string slug, CancellationToken ct) =>
            _views == null ? Task.FromResult(new TrueGamingPage()) : GetPageAsync(site, slug, ct);

        /// <summary>The game's achievement titles in English; empty when there are none to compare.</summary>
        private static async Task<IReadOnlyCollection<string>> EnglishTitlesAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var english = request?.EnglishTitles == null ? null : await request.EnglishTitles(ct).ConfigureAwait(false);
            return (english ?? new List<AchievementDetail>())
                .Select(row => row?.DisplayName)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToList();
        }

        /// <summary>The page each site gave the link ("TrueTrophies: Elden-Ring-PS5"), "None" where it has none.</summary>
        public string DescribeLink(Guid gameId, string linkId)
        {
            var name = StoreName(linkId);
            var pages = name == null ? Array.Empty<(TrueGamingSite Site, string PageSlug)>() : _store.GetPages(gameId, name);
            return pages.Count == 0
                ? null
                : string.Join(Environment.NewLine, pages.Select(page =>
                    $"{page.Site.Title}: {page.PageSlug ?? ResourceProvider.GetString("LOCPlayAch_Common_None")}"));
        }

        /// <summary>
        /// The link's own page, then each other site's page the link led to, as kept in the page
        /// store; sites without one are left out.
        /// </summary>
        public IReadOnlyList<MetadataPageLink> PageLinks(Guid gameId, string linkId, string servicingProviderKey)
        {
            var name = StoreName(linkId);
            var picked = TrueGamingLinks.Picked(linkId);
            var stored = name == null ? Array.Empty<(TrueGamingSite Site, string PageSlug)>() : _store.GetPages(gameId, name);
            return (picked.Site == null ? Enumerable.Empty<(TrueGamingSite Site, string PageSlug)>() : new[] { (picked.Site, picked.Slug) })
                .Concat(stored.Where(page => page.Site != picked.Site))
                .Where(page => page.PageSlug != null)
                .Select(page => new MetadataPageLink(page.Site.Title, page.Site.ListUrl(page.PageSlug)))
                .ToList();
        }

        /// <summary>The consoles of the edition pages the link led to, in lookup order; null when every page is the plain one.</summary>
        public string ShownPlatform(Guid gameId, string linkId)
        {
            var name = StoreName(linkId);
            var platforms = name == null
                ? new List<string>()
                : _store.GetPages(gameId, name)
                    .Select(page => page.PageSlug == null ? null : page.Site.PlatformOf(page.PageSlug))
                    .Where(platform => platform != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            return platforms.Count == 0 ? null : string.Join(", ", platforms);
        }

        /// <summary>
        /// A picked page as its site and slug ("TrueTrophies: Elden-Ring-PS4"); else the link's one
        /// page name, also for a list or address stored before links were one name.
        /// </summary>
        public string ShownLink(string linkId)
        {
            var picked = TrueGamingLinks.Picked(linkId);
            return picked.Site != null ? $"{picked.Site.Title}: {picked.Slug}" : TrueGamingLinks.Normalize(linkId) ?? linkId;
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

            // Unread, not cached: the next run tries the site again.
            if (IsChallenged(site))
            {
                return new TrueGamingPage();
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

                    var source = await ReadPastChallengeAsync(view.GetPageSourceAsync, ct).ConfigureAwait(false);
                    return ((source, late), late);
                }, ct).ConfigureAwait(false);
                page = TrueGamingPageParser.Parse(html);
                page.Loaded = !string.IsNullOrWhiteSpace(html) && !IsChallenge(html);
                page.TimedOut = timedOut;
                if (!page.Loaded)
                {
                    _logger?.Debug($"[TrueGamingMetadata] {url} could not be read{(string.IsNullOrWhiteSpace(html) ? string.Empty : " (challenge page)")}.");
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        MarkChallenged(site);
                    }
                }
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
                _pages.Set(url, page);
            }

            return page;
        }

        // Cloudflare's browser check clears itself and reloads the page within a few seconds.
        private const int ChallengeAttempts = 3;
        private const int ChallengeRetryDelayMs = 3000;

        /// <summary>The page's source, read again while it is a challenge page, up to a few seconds.</summary>
        internal static async Task<string> ReadPastChallengeAsync(Func<Task<string>> read, CancellationToken ct)
        {
            var source = await read().ConfigureAwait(false);
            for (var attempt = 1; source != null && IsChallenge(source) && attempt < ChallengeAttempts; attempt++)
            {
                await Task.Delay(ChallengeRetryDelayMs, ct).ConfigureAwait(false);
                source = await read().ConfigureAwait(false);
            }

            return source;
        }

        internal static bool IsChallenge(string html) =>
            html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("Attention Required", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
