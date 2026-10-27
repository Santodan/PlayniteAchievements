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
    /// Achievement flags (as tags), DLC groups (as categories), gamerscore and trophy ranks from
    /// the TrueGaming sites: TrueAchievements, TrueTrophies and TrueSteamAchievements, tried in that
    /// order for every platform. Pages are public and load through an offscreen browser view. A
    /// game links to one page name ("Fallout-4"), built from its name; each site is read under it
    /// or its edition variants there ("Fallout-4-PS4"), and where it led is kept in
    /// <see cref="TrueGamingPageStore"/>. Only the pages the wanted fields need are loaded; see
    /// <see cref="TrueGamingPageSet"/>. The sites are English only, so rows pair with the game's
    /// achievements by English title.
    /// </summary>
    internal sealed class TrueGamingMetadataProvider : IMetadataProvider, IMetadataLinkDetails, IMetadataPageLinks, IMetadataLinkSearch, IMetadataLinkPlatform, IMetadataServedPages
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
        /// The game's one page name, from the first page found by slugs built from its names.
        /// Only the pages the wanted fields need are looked for; what each site gave is kept.
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
                var search = new MetadataPageSearch(
                    (name, token) => CandidatesAsync(name, health, token),
                    (slug, token) => RowsAsync(request, slug, health, token),
                    (name, token) => CandidatesAsync(name, health, token, buildSlug: false));
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

                // A new lookup (a new game, or a reset link) starts the game's pages over.
                _store.Clear(GameId(request));
                var chosen = MetadataLinkIds.Join(choice.Ids.Select(TrueGamingLinks.BaseSlug));
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
            var linkId = TrueGamingLinks.BaseSlug(found[0].Slug);
            _store.Clear(GameId(request));
            Record(request, linkId, outcomes);
            _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {linkId} ({Describe(found)}).");
            return new MetadataSourceLink(linkId, request.Game?.Name, foundByName: true);
        }

        // TrueAchievements search results judged per name; each can cost a page load on every site.
        private const int SearchCandidatesJudged = 3;

        private readonly Dictionary<string, IReadOnlyList<(string Slug, string Title)>> _searches =
            new Dictionary<string, IReadOnlyList<(string Slug, string Title)>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The page names to judge for one name: its own slug, then what TrueAchievements search
        /// lists. A broader form of a name ("Spyro Year of the") is searched only: a slug built
        /// from it seldom names a page and costs a load on every site.
        /// </summary>
        private async Task<IReadOnlyList<MetadataPageCandidate>> CandidatesAsync(string name, MetadataLookupHealth health, CancellationToken ct, bool buildSlug = true)
        {
            var candidates = new List<MetadataPageCandidate>();
            var slug = buildSlug ? TrueGamingLinks.ToSlug(name) : null;
            if (slug != null)
            {
                candidates.Add(new MetadataPageCandidate(slug, name, "built name"));
            }

            var results = await SearchAsync(name, health, ct).ConfigureAwait(false);
            for (var i = 0; i < results.Count; i++)
            {
                candidates.Add(new MetadataPageCandidate(TrueGamingLinks.BaseSlug(results[i].Slug), results[i].Title, $"TrueAchievements search #{i + 1}"));
            }

            return candidates;
        }

        /// <summary>TrueAchievements' search results for a name, asked once per run; a search that did not answer is not kept.</summary>
        private async Task<IReadOnlyList<(string Slug, string Title)>> SearchAsync(string name, MetadataLookupHealth health, CancellationToken ct)
        {
            var url = TrueGamingLinks.SearchUrl(name);
            lock (_searches)
            {
                if (_searches.TryGetValue(url, out var cached))
                {
                    return cached;
                }
            }

            if (IsChallenged(TrueGamingSite.TrueAchievements))
            {
                health.MarkUnreadable();
                return Array.Empty<(string, string)>();
            }

            var text = await ReadSearchPageAsync(url, ct).ConfigureAwait(false);
            if (text == null)
            {
                health.MarkUnreadable();
                return Array.Empty<(string, string)>();
            }

            var results = TrueGamingLinks.ParseSearch(text).Take(SearchCandidatesJudged).ToList();
            lock (_searches)
            {
                _searches[url] = results;
            }

            return results;
        }

        /// <summary>
        /// Every game TrueAchievements' search answers for a name typed into the link picker, in
        /// its order, as page names without an edition suffix. Not kept. Null when the search could
        /// not be read.
        /// </summary>
        internal async Task<IReadOnlyList<(string Slug, string Title)>> SearchPagesAsync(string name, CancellationToken ct)
        {
            if (_views == null || string.IsNullOrWhiteSpace(name))
            {
                return _views == null ? null : Array.Empty<(string, string)>();
            }

            var text = await ReadSearchPageAsync(TrueGamingLinks.SearchUrl(name.Trim()), ct).ConfigureAwait(false);
            return text == null
                ? null
                : TrueGamingLinks.ParseSearch(text)
                    .Select(game => (Slug: TrueGamingLinks.BaseSlug(game.Slug), game.Title))
                    .GroupBy(game => game.Slug, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
        }

        /// <summary>A search page's text, or null when it could not be read or was a challenge page.</summary>
        private async Task<string> ReadSearchPageAsync(string url, CancellationToken ct)
        {
            string text;
            try
            {
                text = await _views.WithNavigableViewAsync(async view =>
                {
                    await view.NavigateAndWaitAsync(url, PageTimeoutMs).ConfigureAwait(false);
                    var source = await ReadPastChallengeAsync(view.GetPageSourceAsync, ct).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(source) || IsChallenge(source)
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
                    MarkChallenged(TrueGamingSite.TrueAchievements);
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

            var found = new List<TrueGamingFoundPage>();
            var failed = new HashSet<TrueGamingSite>();
            var outcomes = await AddNeededPagesAsync(request, found, failed, name, ct).ConfigureAwait(false);
            Record(request, name, outcomes);
            if (found.Count == 0)
            {
                return null;
            }

            found = found.OrderBy(page => TrueGamingSite.LookupOrder.ToList().IndexOf(page.Site)).ToList();
            var combined = TrueGamingPageSet.CombineAll(found, request.BaseCategoryLabel);
            var set = new AchievementSourceSet(Key, linkId, combined.Rows)
            {
                GameArtUrl = found.Select(page => page.Page.GameArt).FirstOrDefault(art => art != null),
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

                // A page picked for the choice this site serves is read as named, whatever the link's name.
                var picked = PickedSlug(request, site);
                if (picked != null)
                {
                    var pickedPage = await GetPageAsync(site, picked, ct).ConfigureAwait(false);
                    if (pickedPage.Achievements.Count > 0)
                    {
                        found.Add(new TrueGamingFoundPage(site, picked, pickedPage));
                    }
                    else
                    {
                        failed.Add(site);
                    }

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

                settled &= page.Loaded;
            }

            return (null, settled);
        }

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
            var name = TrueGamingLinks.Normalize(linkId);
            var pages = name == null ? Array.Empty<(TrueGamingSite Site, string PageSlug)>() : _store.GetPages(gameId, name);
            return pages.Count == 0
                ? null
                : string.Join(Environment.NewLine, pages.Select(page =>
                    $"{page.Site.Title}: {page.PageSlug ?? ResourceProvider.GetString("LOCNone")}"));
        }

        /// <summary>Each site's page the link led to, as kept in the page store; sites without one are left out.</summary>
        public IReadOnlyList<MetadataPageLink> PageLinks(Guid gameId, string linkId, string servicingProviderKey)
        {
            var name = TrueGamingLinks.Normalize(linkId);
            return name == null
                ? Array.Empty<MetadataPageLink>()
                : _store.GetPages(gameId, name)
                    .Where(page => page.PageSlug != null)
                    .Select(page => new MetadataPageLink(page.Site.Title, page.Site.ListUrl(page.PageSlug)))
                    .ToList();
        }

        /// <summary>The site a choice's fields are read from: Xbox points from TrueAchievements, PlayStation ranks from TrueTrophies.</summary>
        private static TrueGamingSite ServedSite(string choiceKey)
        {
            var key = choiceKey?.Trim();
            return string.Equals(key, MetadataChoices.Xbox, StringComparison.OrdinalIgnoreCase) ? TrueGamingSite.TrueAchievements
                : string.Equals(key, MetadataChoices.PlayStation, StringComparison.OrdinalIgnoreCase) ? TrueGamingSite.TrueTrophies
                : null;
        }

        private static string ServedChoice(TrueGamingSite site) =>
            site == TrueGamingSite.TrueAchievements ? MetadataChoices.Xbox
            : site == TrueGamingSite.TrueTrophies ? MetadataChoices.PlayStation
            : null;

        private static string PageKey(string choice) => choice == null ? null : MetadataKey + "." + choice;

        /// <summary>The page picked for the choice <paramref name="site"/> serves, from the game's links; null when none is.</summary>
        private static string PickedSlug(MetadataLinkRequest request, TrueGamingSite site)
        {
            var key = PageKey(ServedChoice(site));
            return key != null && request?.OtherLinks != null && request.OtherLinks.TryGetValue(key, out var id)
                ? TrueGamingLinks.ExactSlug(site, id)
                : null;
        }

        public string ServedPageKey(string choiceKey) => PageKey(ServedChoice(ServedSite(choiceKey)));

        public MetadataServedPage ServedPage(Guid gameId, string choiceKey, string pickedId, string sourceLinkId)
        {
            var site = ServedSite(choiceKey);
            if (site == null)
            {
                return null;
            }

            var slug = TrueGamingLinks.ExactSlug(site, pickedId);
            var name = TrueGamingLinks.Normalize(sourceLinkId);
            if (slug == null && name != null && _store.TryGet(gameId, name, site, out var known))
            {
                slug = known;
            }

            return slug == null
                ? null
                : new MetadataServedPage(slug, (TrueGamingLinks.BaseSlug(slug) ?? slug).Replace('-', ' '), site.ListUrl(slug), site.PlatformOf(slug));
        }

        public IGameSearchSource CreateServedPageSearch(string choiceKey, string displayName)
        {
            var site = ServedSite(choiceKey);
            return _views == null || site == null ? null : new TrueGamingPageSearch(this, site, displayName);
        }

        /// <summary>The consoles of the edition pages the link led to, in lookup order; null when every page is the plain one.</summary>
        public string ShownPlatform(Guid gameId, string linkId)
        {
            var name = TrueGamingLinks.Normalize(linkId);
            var platforms = name == null
                ? new List<string>()
                : _store.GetPages(gameId, name)
                    .Select(page => page.PageSlug == null ? null : page.Site.PlatformOf(page.PageSlug))
                    .Where(platform => platform != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            return platforms.Count == 0 ? null : string.Join(", ", platforms);
        }

        /// <summary>The link's one page name, also for a list or address stored before links were one name.</summary>
        public string ShownLink(string linkId) => TrueGamingLinks.Normalize(linkId) ?? linkId;

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
