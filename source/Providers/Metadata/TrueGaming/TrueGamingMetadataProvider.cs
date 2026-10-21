using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Achievement flags (as tags) and DLC groups (as categories) from the TrueGaming sites:
    /// TrueAchievements for Xbox games, TrueTrophies for PlayStation games and
    /// TrueSteamAchievements for the rest. Pages are public and load through an offscreen browser
    /// view. A game links to a page slug ("Fallout-4"), built from its name, or a pasted page
    /// address, which may name another of the three sites. The sites are English only, so rows
    /// pair with the game's achievements by English title.
    /// </summary>
    internal sealed class TrueGamingMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "TrueGaming";

        private readonly OffscreenViewLeaseSource _views;
        private readonly ILogger _logger;
        private readonly Dictionary<string, TrueGamingPage> _pages =
            new Dictionary<string, TrueGamingPage>(StringComparer.OrdinalIgnoreCase);
        private IDisposable _lease;

        public TrueGamingMetadataProvider(IPlayniteAPI api, ILogger logger)
        {
            _views = api == null ? null : new OffscreenViewLeaseSource(api, logger);
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_TrueGaming";

        public string LinkPlaceholder => "Game-Name";

        public MetadataField SupportedFields => MetadataField.Tags | MetadataField.Categories | MetadataField.CategoryArt;

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

        /// <summary>The first slug built from the game's names whose page lists achievements.</summary>
        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            if (_views == null || request == null)
            {
                return null;
            }

            var site = TrueGamingSite.For(request.ServicingProviderKey);
            foreach (var slug in TrueGamingLinks.CandidateSlugs(request.SearchNames, site.Suffixes))
            {
                var page = await GetPageAsync(site, slug, ct).ConfigureAwait(false);
                if (page.Achievements.Count > 0)
                {
                    _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}' -> {site.Host}/game/{slug}.");
                    return new MetadataSourceLink(slug, request.Game?.Name, foundByName: true);
                }
            }

            _logger?.Debug($"[TrueGamingMetadata] '{request.Game?.Name}': no {site.Host} page.");
            return null;
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (_views == null || !TrueGamingLinks.TryParse(linkId, request?.ServicingProviderKey, out var site, out var slug))
            {
                return null;
            }

            var page = await GetPageAsync(site, slug, ct).ConfigureAwait(false);
            if (page.Achievements.Count == 0)
            {
                return null;
            }

            if (!page.FlagsDecoded)
            {
                _logger?.Warn($"[TrueGamingMetadata] {site.Host}/game/{slug}: flags could not be read; rows carry no tags.");
            }

            var set = new AchievementSourceSet(Key, linkId, page.Achievements.Select(row => ToRow(row, request?.BaseCategoryLabel)).ToList());
            foreach (var art in page.DlcArt)
            {
                var label = CategoryPathHelper.SanitizeSegment(art.Key);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    set.CategoryArtUrls[label] = art.Value;
                }
            }

            return set;
        }

        /// <summary>A DLC pack's rows go under the pack's name; the base game's under <paramref name="baseLabel"/>.</summary>
        private static AchievementDetail ToRow(TrueGamingAchievement row, string baseLabel)
        {
            var dlc = CategoryPathHelper.SanitizeSegment(row.Dlc);
            return new AchievementDetail
            {
                ApiName = row.Id,
                DisplayName = row.Name,
                Description = row.Description,
                Category = string.IsNullOrWhiteSpace(dlc) ? baseLabel : dlc,
                CategoryKind = string.IsNullOrWhiteSpace(dlc) ? CategoryKindHelper.Base : CategoryKindHelper.Dlc,
                Tags = TrueGamingFlags.ToTags(row.Flags)
            };
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
                    await view.NavigateAndWaitAsync(url).ConfigureAwait(false);
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
