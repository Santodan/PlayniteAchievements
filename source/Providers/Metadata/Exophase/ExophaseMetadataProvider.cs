using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// The Exophase metadata sources: one per <see cref="ExophaseMetadataBranch"/>, sharing one page
    /// source so a page two of them read is loaded once.
    /// </summary>
    internal sealed class ExophaseMetadataProviders : IMetadataProviderFamily
    {
        public ExophaseMetadataProviders(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            var pages = new ExophasePageSource(api, settings, logger);
            Providers = ExophaseMetadataBranch.All
                .Select(branch => (IMetadataProvider)new ExophaseMetadataProvider(branch, pages, logger))
                .ToList();
        }

        public IReadOnlyList<IMetadataProvider> Providers { get; }
    }

    /// <summary>
    /// A metadata source read from Exophase's public award pages, in the plugin's language where
    /// Exophase translates them. A borrowing branch links a game to the borrowed platform's page,
    /// replaced by its newest version when the page links one; the own-platform branch to its own
    /// platform's page. Both find it the same way: slugs built from the game's names, then
    /// Exophase search, with the scan's hints (the platform's title for the game, a collection's
    /// trophy set titles, a region).
    /// </summary>
    internal sealed class ExophaseMetadataProvider : IMetadataProvider, IMetadataPageLinks, IMetadataLinkSearch, IMetadataLinkPlatform
    {
        // Search results judged per name; each costs a page load the first time in a run.
        private const int SearchCandidatesJudged = 6;

        private static readonly HashSet<string> PlayStationPlatforms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "psn", "ps3", "ps4", "ps5", "psvita", "vita" };

        private readonly ExophaseMetadataBranch _branch;
        private readonly ExophasePageSource _pages;
        private readonly ILogger _logger;

        internal ExophaseMetadataProvider(ExophaseMetadataBranch branch, ExophasePageSource pages, ILogger logger)
        {
            _branch = branch ?? throw new ArgumentNullException(nameof(branch));
            _pages = pages;
            _logger = logger;
        }

        public string Key => _branch.Key;

        public string DisplayNameKey => _branch.DisplayNameKey;

        public MetadataField SupportedFields => _branch.Fields;

        /// <summary>
        /// Exophase search on the branch's platforms: the borrowed platform's, or the game's own
        /// (any platform when the servicing provider's has no Exophase slug).
        /// </summary>
        public IGameSearchSource CreateLinkSearch(Guid gameId, string servicingProviderKey)
        {
            if (_pages?.IsAvailable != true)
            {
                return null;
            }

            var ownPlatform = ExophaseMetadataBranch.OwnPlatformSlug(servicingProviderKey);
            var platforms = !_branch.OwnPlatform
                ? _branch.SearchPlatforms
                : ownPlatform == null ? Array.Empty<string>() : PlatformCandidates(ownPlatform);
            return new ExophaseLinkSearch(_pages, platforms, ResourceProvider.GetString(DisplayNameKey));
        }

        public bool IsNativeFor(string servicingProviderKey) =>
            !string.IsNullOrWhiteSpace(servicingProviderKey) && _branch.NativeProviders.Contains(servicingProviderKey);

        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            if (_pages?.IsAvailable != true)
            {
                return Task.FromResult(false);
            }

            _pages.BeginRun();
            return Task.FromResult(true);
        }

        public void EndRun() => _pages?.EndRun();

        public Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_pages?.IsAvailable != true || request == null)
            {
                return Task.FromResult<MetadataSourceLink>(null);
            }

            return ResolvePagesAsync(request, ct);
        }

        /// <summary>
        /// The game's page, found the same way on every branch: the slugs built from its names
        /// (the platform's own title for it first, when the scan gave one) with the platform's
        /// suffixes, then Exophase search for those names, preferring the scan's region. A
        /// collection gets one page per trophy set, found by that set's title alone. A borrowed
        /// page that links the preferred platform's version of the same game gives way to it.
        /// </summary>
        private async Task<MetadataSourceLink> ResolvePagesAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var pagePlatform = _branch.OwnPlatform ? OwnPlatformSlug(request) : _branch.PlatformHint;
            if (pagePlatform == null)
            {
                return null;
            }

            var hints = request.Data?.MetadataHints;
            var suffixes = _branch.OwnPlatform ? OwnPageSuffixes(pagePlatform) : _branch.PageSuffixes;
            var searchPlatforms = _branch.OwnPlatform ? PlatformCandidates(pagePlatform) : _branch.SearchPlatforms;
            if (_branch.OwnPlatform && hints?.SetTitles?.Count > 0)
            {
                var slugs = new List<string>();
                foreach (var title in hints.SetTitles.Where(title => !string.IsNullOrWhiteSpace(title)))
                {
                    var (slug, unreadable) = await FindPageAsync(new[] { title }, suffixes, searchPlatforms, pagePlatform, hints.RegionHint, ct).ConfigureAwait(false);
                    if (slug != null)
                    {
                        slugs.Add(slug);
                    }
                    else if (unreadable)
                    {
                        // A set left out because its pages went unread would be missing from the
                        // stored link for good, so the whole link waits for the next refresh.
                        _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}': the pages for set '{title}' could not be read; looking again next refresh.");
                        return MetadataSourceLink.Unavailable();
                    }
                }

                return slugs.Count == 0
                    ? null
                    : new MetadataSourceLink(MetadataLinkIds.Join(slugs), request.Game?.Name, foundByName: true);
            }

            var names = new[] { hints?.SearchName }.Concat(request.SearchNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => ExophaseGameNameMatcher.NormalizeGameName(name) ?? name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            // With the game's achievements at hand, every page the searches offer is judged by
            // how many of them it matches, and a collection is also looked for game by game.
            var targets = request.Data?.Achievements?.Where(row => row != null).ToList();
            if (targets?.Count > 0)
            {
                var health = new MetadataLookupHealth();
                var search = new MetadataPageSearch(
                    (name, token) => CandidatesAsync(name, suffixes, searchPlatforms, pagePlatform, health, token),
                    async (slug, token) =>
                    {
                        var page = await _pages.GetPageAsync(slug, pagePlatform, token).ConfigureAwait(false);
                        if (ExophasePageSource.IsUnreadable(page))
                        {
                            health.MarkUnreadable();
                        }

                        return page?.Achievements;
                    },
                    englishRows: async (slug, token) => (await _pages.GetPageAsync(slug, pagePlatform, token, english: true).ConfigureAwait(false))?.Achievements);
                var seeds = await RelatedSeedsAsync(request, ct).ConfigureAwait(false);
                var choice = await search.ChooseAsync(names, targets, MetadataSubGame.From(targets), ct, request.CategoryNames, seeds, request.AlternateNames, request.EnglishTitles).ConfigureAwait(false);
                if (health.LeavesDoubt(choice))
                {
                    _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}': {choice?.Matched ?? 0}/{targets.Count} matched, but some pages could not be read; looking again next refresh.");
                    return MetadataSourceLink.Unavailable(choice?.Trace);
                }

                if (choice == null)
                {
                    _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}': no {pagePlatform} page matches its achievements.");
                    return null;
                }

                var chosen = new List<string>();
                foreach (var slug in choice.Ids)
                {
                    chosen.Add(_branch.OwnPlatform ? slug : await PreferredVersionAsync(slug, ct).ConfigureAwait(false));
                }

                var linkId = MetadataLinkIds.Join(chosen);
                _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}' -> {linkId} ({choice.Matched}/{targets.Count} matched; {choice.Trace}).");
                return new MetadataSourceLink(linkId, choice.Label ?? request.Game?.Name, foundByName: true) { Trace = choice.Trace, Matched = choice.Matched };
            }

            var (found, pagesUnread) = await FindPageAsync(names, suffixes, searchPlatforms, pagePlatform, hints?.RegionHint, ct).ConfigureAwait(false);
            if (found == null)
            {
                if (pagesUnread)
                {
                    _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}': no {pagePlatform} page found, but some pages could not be read; looking again next refresh.");
                    return MetadataSourceLink.Unavailable();
                }

                _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}': no {pagePlatform} page.");
                return null;
            }

            if (!_branch.OwnPlatform)
            {
                found = await PreferredVersionAsync(found, ct).ConfigureAwait(false);
            }

            _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}' -> {found}.");
            return new MetadataSourceLink(found, request.Game?.Name, foundByName: true);
        }

        /// <summary>
        /// The first built slug whose page lists awards, else the first search match; null when
        /// neither finds one. Unreadable is true when a page or search along the way could not be
        /// read, so finding nothing does not mean the game has no page.
        /// </summary>
        private async Task<(string Slug, bool Unreadable)> FindPageAsync(
            IReadOnlyList<string> names,
            IReadOnlyList<string> suffixes,
            IReadOnlyList<string> searchPlatforms,
            string pagePlatform,
            string regionHint,
            CancellationToken ct)
        {
            var unreadable = false;
            foreach (var slug in CandidateSlugs(names, suffixes))
            {
                var page = await _pages.GetPageAsync(slug, pagePlatform, ct).ConfigureAwait(false);
                if (page?.Achievements?.Count > 0)
                {
                    return (slug, false);
                }

                unreadable |= ExophasePageSource.IsUnreadable(page);
            }

            foreach (var name in names)
            {
                var (slug, answered) = await _pages.SearchSlugAsync(name, searchPlatforms, regionHint, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    return (slug, false);
                }

                unreadable |= !answered;
            }

            return (null, unreadable);
        }

        /// <summary>
        /// This platform's pages for the game that the game's other Exophase links point to: each
        /// linked page's links to the same game on other platforms ("Also on"). Judged before any
        /// search, at the cost of the linked pages, which the overlay reads anyway.
        /// </summary>
        private async Task<IReadOnlyList<MetadataPageCandidate>> RelatedSeedsAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var family = RelatedPlatformPrefix(_branch);
            var seeds = new List<MetadataPageCandidate>();
            if (family == null || request.OtherLinks == null)
            {
                return seeds;
            }

            foreach (var branch in ExophaseMetadataBranch.All.Where(branch => branch != _branch))
            {
                if (!request.OtherLinks.TryGetValue(branch.Key, out var linkId))
                {
                    continue;
                }

                var platform = branch.OwnPlatform ? OwnPlatformSlug(request) : branch.PlatformHint;
                if (platform == null)
                {
                    continue;
                }

                foreach (var slug in MetadataLinkIds.Parse(linkId).Select(PageSlug).Where(slug => slug != null))
                {
                    var page = await _pages.GetPageAsync(slug, platform, ct).ConfigureAwait(false);
                    seeds.AddRange((page?.RelatedPages ?? Array.Empty<ExophaseRelatedPage>())
                        .Where(related => related.Platform.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                        .Select(related => new MetadataPageCandidate(related.Slug, null, $"{branch.Key}'s Also on")));
                }
            }

            return seeds;
        }

        /// <summary>How "Also on" names the branch's platforms ("PS5", "PS Vita"; "Xbox One"); null for the own-platform branch.</summary>
        private static string RelatedPlatformPrefix(ExophaseMetadataBranch branch) =>
            branch == ExophaseMetadataBranch.PlayStation ? "PS"
            : branch == ExophaseMetadataBranch.Xbox ? "Xbox"
            : null;

        /// <summary>
        /// The pages to judge for one name: what Exophase search offers on the branch's platforms,
        /// else the slugs built from the name. A built slug costs a page load whether or not the
        /// page exists, while the search names the real pages, so building waits for an empty search.
        /// </summary>
        private async Task<IReadOnlyList<MetadataPageCandidate>> CandidatesAsync(
            string name,
            IReadOnlyList<string> suffixes,
            IReadOnlyList<string> searchPlatforms,
            string pagePlatform,
            MetadataLookupHealth health,
            CancellationToken ct)
        {
            var (found, answered) = await _pages.SearchCandidatesAsync(name, searchPlatforms, SearchCandidatesJudged, ct).ConfigureAwait(false);
            if (!answered)
            {
                health.MarkUnreadable();
            }

            if (found.Count > 0)
            {
                return found.Select((page, index) => new MetadataPageCandidate(page.Slug, page.Title, $"search #{index + 1}")).ToList();
            }

            var candidates = new List<MetadataPageCandidate>();
            foreach (var slug in CandidateSlugs(new[] { name }, suffixes))
            {
                var page = await _pages.GetPageAsync(slug, pagePlatform, ct).ConfigureAwait(false);
                if (page?.Achievements?.Count > 0)
                {
                    candidates.Add(new MetadataPageCandidate(slug, name, "built name"));
                }
                else if (ExophasePageSource.IsUnreadable(page))
                {
                    health.MarkUnreadable();
                }
            }

            return candidates;
        }

        /// <summary>The preferred platform's version of a borrowed page (the PS5 page of a PS4 game), when the page links one that lists awards.</summary>
        private async Task<string> PreferredVersionAsync(string slug, CancellationToken ct)
        {
            if (_branch.PreferredPlatform == null)
            {
                return slug;
            }

            var page = await _pages.GetPageAsync(slug, _branch.PlatformHint, ct).ConfigureAwait(false);
            var preferred = (page?.RelatedPages ?? Array.Empty<ExophaseRelatedPage>())
                .FirstOrDefault(related => string.Equals(related.Platform, _branch.PreferredPlatform, StringComparison.OrdinalIgnoreCase))
                ?.Slug;
            if (string.IsNullOrWhiteSpace(preferred) || string.Equals(preferred, slug, StringComparison.OrdinalIgnoreCase))
            {
                return slug;
            }

            var preferredPage = await _pages.GetPageAsync(preferred, _branch.PlatformHint, ct).ConfigureAwait(false);
            return preferredPage?.Achievements?.Count > 0 ? preferred : slug;
        }

        /// <summary>
        /// The own platform's slug suffixes: the platform itself ("-xbox-360", "-origin"), both
        /// Xbox spellings for current Xbox games, and none for PlayStation, whose slugs carry no
        /// platform suffix.
        /// </summary>
        private static IReadOnlyList<string> OwnPageSuffixes(string platformSlug)
        {
            if (PlayStationPlatforms.Contains(platformSlug))
            {
                return new[] { string.Empty };
            }

            return PlatformCandidates(platformSlug).Select(platform => "-" + platform).ToList();
        }
        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (_pages?.IsAvailable != true)
            {
                return null;
            }

            var platformSlug = _branch.OwnPlatform ? OwnPlatformSlug(request) : _branch.PlatformHint;
            if (platformSlug == null)
            {
                return null;
            }

            // One page; the overlay fetches each page of a link that names several.
            var slug = PageSlug(linkId);
            if (slug == null)
            {
                return null;
            }

            var page = await _pages.GetPageAsync(slug, platformSlug, ct).ConfigureAwait(false);
            var rows = page?.Achievements ?? new List<AchievementDetail>();
            var categoryArt = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            var gameArt = page?.GameArtUrl;
            var baseLabel = request?.BaseCategoryLabel;
            ApplySections(rows, page?.Sections ?? Array.Empty<ExophaseAwardSection>(), categoryArt, baseLabel);

            // The page's game image is its base list's art; a link to several pages gives each
            // page's image to its own game's category instead.
            if (!string.IsNullOrWhiteSpace(gameArt) && !string.IsNullOrWhiteSpace(baseLabel) && !categoryArt.ContainsKey(baseLabel))
            {
                categoryArt[baseLabel] = gameArt.Trim();
            }

            if (rows.Count == 0)
            {
                return null;
            }

            var set = new AchievementSourceSet(Key, linkId, rows)
            {
                GameArtUrl = gameArt,
                EnglishRows = async token => (await _pages.GetPageAsync(slug, platformSlug, token, english: true).ConfigureAwait(false))?.Achievements,
                TextInPluginLanguage = _pages.IsInPluginLanguage(page)
            };
            foreach (var art in categoryArt)
            {
                set.CategoryArtUrls[art.Key] = art.Value;
            }

            return set;
        }

        /// <summary>The linked page's award page, on the branch's platform.</summary>
        public IReadOnlyList<MetadataPageLink> PageLinks(Guid gameId, string linkId, string servicingProviderKey)
        {
            var platformSlug = _branch.OwnPlatform ? ExophaseMetadataBranch.OwnPlatformSlug(servicingProviderKey) : _branch.PlatformHint;
            var slug = PageSlug(linkId);
            var url = platformSlug == null || slug == null ? null : ExophaseApiClient.BuildUrlFromSlug(slug, platformSlug);
            return string.IsNullOrWhiteSpace(url)
                ? Array.Empty<MetadataPageLink>()
                : new[] { new MetadataPageLink(slug, url) };
        }

        /// <summary>The own platform's Exophase slug: the scan's hint ("xbox-360" for a 360 game), else the servicing platform's.</summary>
        private static string OwnPlatformSlug(MetadataLinkRequest request)
        {
            var hinted = request?.Data?.MetadataHints?.PlatformSlug;
            return !string.IsNullOrWhiteSpace(hinted)
                ? hinted.Trim().ToLowerInvariant()
                : ExophaseMetadataBranch.OwnPlatformSlug(request?.ServicingProviderKey);
        }

        /// <summary>The platform slugs a search tries: Exophase lists current Xbox games under both xbox and xbox-one.</summary>
        private static IReadOnlyList<string> PlatformCandidates(string platformSlug)
        {
            switch (platformSlug)
            {
                case "xbox":
                    return new[] { "xbox", "xbox-one" };
                case "xbox-one":
                    return new[] { "xbox-one", "xbox" };
                default:
                    return new[] { platformSlug };
            }
        }

        /// <summary>
        /// Files each row under its page's list: Base under <paramref name="baseLabel"/> for the
        /// game's own, DLC under the DLC's name for the rest, with the DLC's own image as its
        /// category art.
        /// </summary>
        private static void ApplySections(
            IList<AchievementDetail> rows,
            IReadOnlyList<ExophaseAwardSection> sections,
            IDictionary<string, string> categoryArt,
            string baseLabel)
        {
            var byApiName = new Dictionary<string, ExophaseAwardSection>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in sections ?? Array.Empty<ExophaseAwardSection>())
            {
                foreach (var apiName in section.ApiNames)
                {
                    byApiName[apiName] = section;
                }

                var label = CategoryPathHelper.SanitizeSegment(section.Title);
                if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(section.ArtUrl))
                {
                    categoryArt[label] = section.ArtUrl;
                }
            }

            foreach (var row in rows)
            {
                if (row?.ApiName == null || !byApiName.TryGetValue(row.ApiName, out var section))
                {
                    continue;
                }

                var isDlc = !string.IsNullOrWhiteSpace(section.Title);
                row.CategoryKind = isDlc ? CategoryKindHelper.Dlc : CategoryKindHelper.Base;
                row.Category = isDlc ? CategoryPathHelper.SanitizeSegment(section.Title) : baseLabel;
            }
        }

        /// <summary>
        /// The page slugs to try for each name in turn, accents removed and without the hyphen a
        /// leading bracket leaves ("(the) Gnorp Apologue"), without repeats.
        /// </summary>
        internal static IEnumerable<string> CandidateSlugs(IEnumerable<string> names, IEnumerable<string> suffixes)
        {
            var endings = (suffixes ?? Enumerable.Empty<string>()).ToList();
            return (names ?? Enumerable.Empty<string>())
                .Select(name => ExophaseGameNameMatcher.NormalizeGameNameForSlug(GameNameNormalizer.StripDiacritics(name ?? string.Empty))?.Trim('-'))
                .Where(slug => !string.IsNullOrWhiteSpace(slug))
                .SelectMany(slug => endings.Select(suffix => slug + suffix))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The console the list's page slug names, for a borrowed platform's list; the own platform's needs none.</summary>
        public string ShownPlatform(Guid gameId, string linkId) =>
            _branch.OwnPlatform ? null : ExophaseMetadataBranch.PlatformOfSlug(PageSlug(linkId));

        /// <summary>The page slug one list of a link names; a full page address is accepted. Null when blank.</summary>
        internal static string PageSlug(string linkId)
        {
            var part = linkId?.Trim();
            if (part != null && part.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                part = ExophaseApiClient.ExtractSlugFromUrl(part)?.Trim();
            }

            return string.IsNullOrWhiteSpace(part) ? null : part;
        }
    }
}
