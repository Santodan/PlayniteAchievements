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
    /// game links to the pages it needs, each a slug built from its name ("Fallout-4", or
    /// "tt:Fallout-4" on a site other than the platform's own), comma-separated. Only the pages the
    /// wanted fields need are loaded; see <see cref="TrueGamingPageSet"/>. The sites are English
    /// only, so rows pair with the game's achievements by English title.
    /// </summary>
    internal sealed class TrueGamingMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "TrueGaming";

        // Pages load in under 2 s; one that takes longer is treated as not loaded.
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

        /// <summary>The pages the wanted fields need, found by slugs built from the game's names.</summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_views == null || request == null)
            {
                return null;
            }

            var found = new List<TrueGamingFoundPage>();
            await AddNeededPagesAsync(request, found, new HashSet<TrueGamingSite>(), rememberMisses: false, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': no TrueGaming page.");
                return null;
            }

            var linkId = TrueGamingLinks.Format(found.Select(page => (page.Site, page.Slug)), request.ServicingProviderKey);
            _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {linkId}.");
            return new MetadataSourceLink(linkId, request.Game?.Name, foundByName: true);
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            var linked = TrueGamingLinks.ParsePages(linkId, request?.ServicingProviderKey);
            if (_views == null || linked.Count == 0)
            {
                return null;
            }

            var found = new List<TrueGamingFoundPage>();
            var failed = new HashSet<TrueGamingSite>();
            foreach (var (site, slug) in linked)
            {
                var page = await GetPageAsync(site, slug, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0)
                {
                    found.Add(new TrueGamingFoundPage(site, slug, page));
                }
                else
                {
                    failed.Add(site);
                }
            }

            // An automatic link gains the pages that fields chosen since it was made need, such as
            // TrueTrophies once TrueGaming supplies trophy ranks. A user's link is read as set.
            var extended = false;
            if (!request.LinkIsUserSet && found.Count > 0)
            {
                var before = found.Count;
                var linkedSites = new HashSet<TrueGamingSite>(linked.Select(page => page.Site));
                await AddNeededPagesAsync(request, found, linkedSites, rememberMisses: true, ct).ConfigureAwait(false);
                extended = found.Count > before;
            }

            if (found.Count == 0)
            {
                return null;
            }

            found = found.OrderBy(page => TrueGamingSite.LookupOrder.ToList().IndexOf(page.Site)).ToList();
            var set = new AchievementSourceSet(Key, linkId, TrueGamingPageSet.Combine(found, request.BaseCategoryLabel));
            if (extended)
            {
                set.ExtendedLinkId = TrueGamingLinks.Format(
                    found.Select(page => (page.Site, page.Slug)).Concat(linked.Where(page => failed.Contains(page.Site))),
                    request.ServicingProviderKey);
            }

            // A linked page that did not load leaves the fields it shares with the others as stored,
            // not partly replaced; tags stay as stored too when no page flags any row.
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
        /// fields still need, skipping <paramref name="skip"/> and sites already found.
        /// </summary>
        private async Task AddNeededPagesAsync(
            MetadataLinkRequest request,
            List<TrueGamingFoundPage> found,
            ISet<TrueGamingSite> skip,
            bool rememberMisses,
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
                if (skip.Contains(site) || found.Any(page => page.Site == site) ||
                    !TrueGamingPageSet.NeedsPage(site, wanted, found.Select(page => page.Page), targets))
                {
                    continue;
                }

                var missKey = $"{request.Game?.Id}|{site.Name}";
                if (rememberMisses && _misses.ContainsKey(missKey))
                {
                    continue;
                }

                var page = await FindPageAsync(site, request, targets, ct).ConfigureAwait(false);
                if (page != null)
                {
                    found.Add(page);
                }
                else if (rememberMisses)
                {
                    _misses[missKey] = true;
                }
            }
        }

        /// <summary>
        /// The first slug built from the game's names whose page is the same game. Edition
        /// suffixes ("-PS5", "-Xbox-One") are tried only on the platform's own site.
        /// </summary>
        private async Task<TrueGamingFoundPage> FindPageAsync(
            TrueGamingSite site,
            MetadataLinkRequest request,
            IReadOnlyCollection<string> targets,
            CancellationToken ct)
        {
            var suffixes = site == TrueGamingSite.For(request.ServicingProviderKey) ? site.Suffixes : Array.Empty<string>();
            foreach (var slug in TrueGamingLinks.CandidateSlugs(request.SearchNames, suffixes))
            {
                var page = await GetPageAsync(site, slug, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0 && TrueGamingPageSet.IsSameGame(page, targets))
                {
                    return new TrueGamingFoundPage(site, slug, page);
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
                var html = await _views.WithNavigableViewAsync(async view =>
                {
                    await view.NavigateAndWaitAsync(url, PageTimeoutMs).ConfigureAwait(false);
                    return await view.GetPageSourceAsync().ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
                page = TrueGamingPageParser.Parse(html);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[TrueGamingMetadata] Loading {url} failed.");
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
