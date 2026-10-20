using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>
    /// A metadata source read from one platform's public Exophase award pages, in the plugin's
    /// language where Exophase translates them. No sign-in is needed. A game links to an Exophase
    /// page slug, built from its name with the platform's page suffixes and replaced by the
    /// platform's newest version when the page links one. Subclasses name the platform.
    /// </summary>
    internal abstract class ExophasePageMetadataProvider : IMetadataProvider
    {
        /// <summary>Separates the pages of a link that spans several.</summary>
        internal const char SetSeparator = ',';

        private readonly ExophaseApiClient _exophase;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly Dictionary<string, ExophaseApiClient.AchievementFetchResult> _pages =
            new Dictionary<string, ExophaseApiClient.AchievementFetchResult>(StringComparer.OrdinalIgnoreCase);

        protected ExophasePageMetadataProvider(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            // No cookie store: public pages only, so no Exophase sign-in is involved.
            _exophase = api == null ? null : new ExophaseApiClient(api, logger, null);
            _settings = settings;
            _logger = logger;
        }

        public abstract string Key { get; }

        public abstract string DisplayNameKey { get; }

        public string LinkPlaceholder => "exophase-game-id";

        public abstract MetadataField SupportedFields { get; }

        /// <summary>The servicing providers whose achievements are already this platform's.</summary>
        protected abstract ICollection<string> NativeProviders { get; }

        /// <summary>The platform's page slug suffixes, newest first ("-psn", "-ps4").</summary>
        protected abstract IReadOnlyList<string> PageSuffixes { get; }

        /// <summary>The platform's Exophase slug ("psn", "xbox"), which picks the award list's address.</summary>
        protected abstract string PlatformHint { get; }

        /// <summary>
        /// The "Also on" platform whose page replaces a match of the same game ("PS5"), for its
        /// larger icons; null to keep the match.
        /// </summary>
        protected abstract string PreferredPlatform { get; }

        public bool IsNativeFor(string servicingProviderKey) =>
            !string.IsNullOrWhiteSpace(servicingProviderKey) && NativeProviders.Contains(servicingProviderKey);

        /// <summary>Leases one shared browser view for the run's page loads.</summary>
        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            if (_exophase == null)
            {
                return Task.FromResult(false);
            }

            _exophase.BeginCookieSession();
            return Task.FromResult(true);
        }

        public void EndRun()
        {
            _exophase?.EndCookieSession();
            lock (_pages)
            {
                _pages.Clear();
            }
        }

        /// <summary>
        /// The first page that exists among the slugs built from the game's names. When that page
        /// links the preferred platform's version of the same game, that page is taken instead.
        /// </summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_exophase == null || request == null)
            {
                return null;
            }

            foreach (var slug in CandidateSlugs(request.SearchNames, PageSuffixes))
            {
                var page = await GetPageAsync(slug, ct).ConfigureAwait(false);
                if (page?.Achievements == null || page.Achievements.Count == 0)
                {
                    continue;
                }

                var chosen = slug;
                var preferred = PreferredPlatform == null
                    ? null
                    : ExophaseGamePageParser.ParseRelatedPages(page.Html)
                        .FirstOrDefault(related => string.Equals(related.Platform, PreferredPlatform, StringComparison.OrdinalIgnoreCase))
                        ?.Slug;
                if (!string.IsNullOrWhiteSpace(preferred) && !string.Equals(preferred, slug, StringComparison.OrdinalIgnoreCase))
                {
                    var preferredPage = await GetPageAsync(preferred, ct).ConfigureAwait(false);
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

        /// <summary>An award page in the plugin's language, loaded once per run.</summary>
        private async Task<ExophaseApiClient.AchievementFetchResult> GetPageAsync(string slug, CancellationToken ct)
        {
            lock (_pages)
            {
                if (_pages.TryGetValue(slug, out var cached))
                {
                    return cached;
                }
            }

            // Each game lists its own subset of languages; one it lacks falls back to English.
            var language = ExophaseGamePageParser.TrophyLanguageCode(_settings?.Persisted?.GlobalLanguage);
            var url = ExophaseApiClient.BuildUrlFromSlug(slug, PlatformHint);
            var page = await _exophase.FetchAchievementsWithHtmlAsync(
                ExophaseGamePageParser.LocalizeAwardPageUrl(url, language), null, ct).ConfigureAwait(false);
            if (language != null && !(page?.Achievements?.Count > 0))
            {
                page = await _exophase.FetchAchievementsWithHtmlAsync(url, null, ct).ConfigureAwait(false);
            }

            lock (_pages)
            {
                _pages[slug] = page;
            }

            return page;
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (_exophase == null)
            {
                return null;
            }

            // One page, or several comma-separated. Rows keep that order, so the first page's
            // platinum is the capstone art.
            var rows = new List<AchievementDetail>();
            var categoryArt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slug in SplitSets(linkId))
            {
                var page = await GetPageAsync(slug, ct).ConfigureAwait(false);
                var pageRows = page?.Achievements ?? new List<AchievementDetail>();
                ApplySections(pageRows, ExophaseGamePageParser.ParseAwardSections(page?.Html), categoryArt);
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

        /// <summary>
        /// Files each row under its page's list: Base for the game's own, DLC under the DLC's name
        /// for the rest, with the DLC's own image as its category art.
        /// </summary>
        private static void ApplySections(
            IList<AchievementDetail> rows,
            IReadOnlyList<ExophaseAwardSection> sections,
            IDictionary<string, string> categoryArt)
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
                row.CategoryType = isDlc ? SteamCategoryLabels.DlcCategoryType : SteamCategoryLabels.BaseCategoryType;
                row.Category = isDlc ? CategoryPathHelper.SanitizeSegment(section.Title) : null;
            }
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
