using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// PlayStation trophy icons and platinum art for games another provider services, read from
    /// Exophase's public trophy pages: original-size icons (512px for PS5), trophy types and DLC
    /// lists, in the plugin's language where Exophase translates them. No sign-in is needed. A
    /// game links to an Exophase PlayStation page slug, built from its name and preferring the PS5 page.
    /// </summary>
    internal sealed class PlayStationMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "PlayStation";

        /// <summary>Separates the pages of a link that spans several.</summary>
        internal const char SetSeparator = ',';

        private static readonly HashSet<string> NativeProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PSN", "RPCS3", "ShadPS4", "Vita3K"
        };

        private readonly ExophaseApiClient _exophase;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly Dictionary<string, ExophaseApiClient.AchievementFetchResult> _pages =
            new Dictionary<string, ExophaseApiClient.AchievementFetchResult>(StringComparer.OrdinalIgnoreCase);

        public PlayStationMetadataProvider(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            // No cookie store: public pages only, so no Exophase sign-in is involved.
            _exophase = api == null ? null : new ExophaseApiClient(api, logger, null);
            _settings = settings;
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_PlayStation";

        public MetadataField SupportedFields => MetadataField.Icons | MetadataField.CapstoneArt;

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
        /// The PlayStation page named after the first of the game's names that has one
        /// (<c>&lt;name&gt;-psn</c>, Exophase's slug for the first PlayStation release). When that
        /// page links a PS5 version of the same game, the PS5 page is taken instead.
        /// </summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_exophase == null || request == null)
            {
                return null;
            }

            foreach (var slug in CandidateSlugs(request.SearchNames))
            {
                var page = await GetPageAsync(slug, ct).ConfigureAwait(false);
                if (page?.Achievements == null || page.Achievements.Count == 0)
                {
                    continue;
                }

                var ps5 = ExophaseGamePageParser.ParseRelatedPages(page.Html)
                    .FirstOrDefault(related => string.Equals(related.Platform, "PS5", StringComparison.OrdinalIgnoreCase))
                    ?.Slug;
                var chosen = slug;
                if (!string.IsNullOrWhiteSpace(ps5))
                {
                    var ps5Page = await GetPageAsync(ps5, ct).ConfigureAwait(false);
                    if (ps5Page?.Achievements?.Count > 0)
                    {
                        chosen = ps5;
                    }
                }

                _logger?.Debug($"[PlayStationMetadata] '{request.Game?.Name}' -> {chosen}.");
                return new MetadataSourceLink(chosen, request.Game?.Name, foundByName: true);
            }

            _logger?.Debug($"[PlayStationMetadata] '{request.Game?.Name}': no PlayStation page.");
            return null;
        }

        /// <summary>
        /// The <c>-psn</c> page slug of each name, accents removed and without the hyphen a leading
        /// bracket leaves ("(the) Gnorp Apologue"), without repeats.
        /// </summary>
        internal static IEnumerable<string> CandidateSlugs(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Select(name => ExophaseGameNameMatcher.NormalizeGameNameForSlug(GameNameNormalizer.StripDiacritics(name ?? string.Empty))?.Trim('-'))
                .Where(slug => !string.IsNullOrWhiteSpace(slug))
                .Select(slug => slug + "-psn")
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A trophy page in the plugin's language, loaded once per run.</summary>
        private async Task<ExophaseApiClient.AchievementFetchResult> GetPageAsync(string slug, CancellationToken ct)
        {
            lock (_pages)
            {
                if (_pages.TryGetValue(slug, out var cached))
                {
                    return cached;
                }
            }

            var language = ExophaseGamePageParser.TrophyLanguageCode(_settings?.Persisted?.GlobalLanguage);
            var url = ExophaseGamePageParser.BuildTrophyPageUrl(slug, language);
            var page = await _exophase.FetchAchievementsWithHtmlAsync(url, null, ct).ConfigureAwait(false);
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
            foreach (var slug in SplitSets(linkId))
            {
                var page = await GetPageAsync(slug, ct).ConfigureAwait(false);
                rows.AddRange(page?.Achievements ?? new List<AchievementDetail>());
            }

            return rows.Count == 0 ? null : new AchievementSourceSet(Key, linkId, rows);
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
