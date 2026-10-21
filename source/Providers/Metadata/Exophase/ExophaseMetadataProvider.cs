using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.Achievements;
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
    /// a slug built from the game's names with that platform's suffixes, replaced by its newest
    /// version when the page links one. The own-platform branch links it to its own platform's
    /// page, found by Exophase search with the scan's hints (the platform's title for the game,
    /// a collection's trophy set titles, a region), then by a slug built from its name.
    /// </summary>
    internal sealed class ExophaseMetadataProvider : IMetadataProvider
    {
        /// <summary>Separates the pages of a link that spans several.</summary>
        internal const char SetSeparator = ',';

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

        public string LinkPlaceholder => "exophase-game-id";

        public MetadataField SupportedFields => _branch.Fields;

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

            return _branch.OwnPlatform ? ResolveOwnPageAsync(request, ct) : ResolveBorrowedPageAsync(request, ct);
        }

        /// <summary>
        /// The first borrowed page that exists among the slugs built from the game's names. When
        /// that page links the preferred platform's version of the same game, that page is taken.
        /// </summary>
        private async Task<MetadataSourceLink> ResolveBorrowedPageAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            foreach (var slug in CandidateSlugs(request.SearchNames, _branch.PageSuffixes))
            {
                var page = await _pages.GetPageAsync(slug, _branch.PlatformHint, ct).ConfigureAwait(false);
                if (page?.Achievements == null || page.Achievements.Count == 0)
                {
                    continue;
                }

                var chosen = slug;
                var preferred = _branch.PreferredPlatform == null
                    ? null
                    : ExophaseGamePageParser.ParseRelatedPages(page.Html)
                        .FirstOrDefault(related => string.Equals(related.Platform, _branch.PreferredPlatform, StringComparison.OrdinalIgnoreCase))
                        ?.Slug;
                if (!string.IsNullOrWhiteSpace(preferred) && !string.Equals(preferred, slug, StringComparison.OrdinalIgnoreCase))
                {
                    var preferredPage = await _pages.GetPageAsync(preferred, _branch.PlatformHint, ct).ConfigureAwait(false);
                    if (preferredPage?.Achievements?.Count > 0)
                    {
                        chosen = preferred;
                    }
                }

                _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}' -> {chosen}.");
                return new MetadataSourceLink(chosen, request.Game?.Name, foundByName: true);
            }

            _logger?.Debug($"[{Key}Metadata] '{request.Game?.Name}': no {Key} page.");
            return null;
        }

        /// <summary>
        /// The game's own platform's page: a collection gets one page per trophy set, found by that
        /// set's title; any other game the search match for the platform's title, then the Playnite
        /// names, then a slug built from the first name (not for PlayStation, whose slugs carry no
        /// platform suffix to build).
        /// </summary>
        private async Task<MetadataSourceLink> ResolveOwnPageAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var platformSlug = OwnPlatformSlug(request);
            if (platformSlug == null)
            {
                return null;
            }

            var hints = request.Data?.MetadataHints;
            var platforms = PlatformCandidates(platformSlug);
            if (hints?.SetTitles?.Count > 0)
            {
                var slugs = new List<string>();
                foreach (var title in hints.SetTitles.Where(title => !string.IsNullOrWhiteSpace(title)))
                {
                    var slug = await _pages.SearchSlugAsync(title, platforms, hints.RegionHint, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(slug))
                    {
                        slugs.Add(slug);
                    }
                }

                return slugs.Count == 0
                    ? null
                    : new MetadataSourceLink(string.Join(SetSeparator.ToString(), slugs.Distinct(StringComparer.OrdinalIgnoreCase)), request.Game?.Name, foundByName: true);
            }

            var names = new[] { hints?.SearchName }.Concat(request.SearchNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => ExophaseGameNameMatcher.NormalizeGameName(name) ?? name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var name in names)
            {
                var slug = await _pages.SearchSlugAsync(name, platforms, hints?.RegionHint, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    _logger?.Debug($"[ExophaseMetadata] '{request.Game?.Name}' -> {slug} (search '{name}').");
                    return new MetadataSourceLink(slug, request.Game?.Name, foundByName: true);
                }
            }

            if (names.Count > 0)
            {
                foreach (var platform in platforms.Where(platform => !PlayStationPlatforms.Contains(platform)))
                {
                    var built = ExophaseGameNameMatcher.NormalizeGameNameForSlug(names[0]);
                    if (string.IsNullOrWhiteSpace(built))
                    {
                        break;
                    }

                    var slug = built + "-" + platform;
                    var page = await _pages.GetPageAsync(slug, platformSlug, ct).ConfigureAwait(false);
                    if (page?.Achievements?.Count > 0)
                    {
                        return new MetadataSourceLink(slug, request.Game?.Name, foundByName: true);
                    }
                }
            }

            _logger?.Debug($"[ExophaseMetadata] '{request.Game?.Name}': no {platformSlug} page.");
            return null;
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

            // One page, or several comma-separated. Rows keep that order, so the first page's
            // platinum is the capstone art.
            var rows = new List<AchievementDetail>();
            var categoryArt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slug in SplitSets(linkId))
            {
                var page = await _pages.GetPageAsync(slug, platformSlug, ct).ConfigureAwait(false);
                var pageRows = page?.Achievements ?? new List<AchievementDetail>();
                ApplySections(pageRows, ExophaseGamePageParser.ParseAwardSections(page?.Html), categoryArt, request?.BaseCategoryLabel);
                rows.AddRange(pageRows);
            }

            if (rows.Count == 0)
            {
                return null;
            }

            var set = new AchievementSourceSet(Key, linkId, rows);
            foreach (var art in categoryArt)
            {
                set.CategoryArtUrls[art.Key] = art.Value;
            }

            return set;
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

        /// <summary>The page slugs in a link: one, or several separated by commas. Full page addresses are accepted.</summary>
        internal static IReadOnlyList<string> SplitSets(string linkId)
        {
            return (linkId ?? string.Empty)
                .Split(new[] { SetSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Select(part => part.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? ExophaseApiClient.ExtractSlugFromUrl(part)
                    : part)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
