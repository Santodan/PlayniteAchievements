using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// SteamHunters categories, types and category art for games another provider services. The
    /// game is linked to a Steam appId, the Steam schema supplies the titles the shared matcher
    /// pairs with the provider's achievements, and SteamHunters groups those schema rows the same
    /// way the Steam provider groups its own. Steam games get this from the Steam scan itself.
    /// </summary>
    internal sealed class SteamHuntersMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "SteamHunters";

        private static readonly Regex SteamAppUrl = new Regex(
            @"(?:store\.steampowered\.com|steamcommunity\.com)/app/(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ManualSourceRegistry _manualSources;
        private readonly SteamDataProvider _steam;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public SteamHuntersMetadataProvider(
            ManualSourceRegistry manualSources,
            SteamDataProvider steam,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _manualSources = manualSources;
            _steam = steam;
            _settings = settings;
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_SteamHunters";

        public MetadataField SupportedFields => MetadataField.Categories | MetadataField.CategoryArt;

        private IManualSource SteamSchema => _manualSources?.GetSourceByKey("Steam");

        private string Language => _settings?.Persisted?.GlobalLanguage;

        public bool IsNativeFor(string servicingProviderKey) =>
            string.Equals(servicingProviderKey, "Steam", StringComparison.OrdinalIgnoreCase);

        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            return Task.FromResult(_steam?.SteamHuntersEnricher != null && SteamSchema != null);
        }

        public void EndRun()
        {
        }

        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var game = request?.Game;
            if (game == null)
            {
                return null;
            }

            if (TryGetManualSteamAppId(game.Id, out var manualAppId))
            {
                return new MetadataSourceLink(manualAppId);
            }

            if (GameCustomDataLookup.TryGetSteamAppIdOverride(game.Id, out var overrideAppId) && overrideAppId > 0)
            {
                return new MetadataSourceLink(overrideAppId.ToString(CultureInfo.InvariantCulture));
            }

            var linked = game.Links?
                .Select(link => SteamAppUrl.Match(link?.Url ?? string.Empty))
                .FirstOrDefault(match => match.Success);
            if (linked != null)
            {
                return new MetadataSourceLink(linked.Groups[1].Value);
            }

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            return await SearchByNameAsync(game.Name, ct).ConfigureAwait(false);
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (!int.TryParse(linkId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
            {
                return null;
            }

            var rows = await SteamSchema.GetAchievementsAsync(linkId, Language, ct).ConfigureAwait(false);
            if (rows == null || rows.Count == 0)
            {
                return null;
            }

            var gameId = request?.Game?.Id;
            var applied = await _steam.SteamHuntersEnricher
                .EnrichAsync(appId, request?.Game?.Name, rows, gameId, ct)
                .ConfigureAwait(false);
            if (!applied)
            {
                return null;
            }

            return new AchievementSourceSet(Key, linkId, rows)
            {
                ApiNamesMayBeShared = true,
                SharesApiNames = gameId.HasValue && TryGetManualSteamAppId(gameId.Value, out var manualAppId) &&
                                 string.Equals(manualAppId, linkId, StringComparison.Ordinal)
            };
        }

        private static bool TryGetManualSteamAppId(Guid gameId, out string appId)
        {
            appId = null;
            if (GameCustomDataLookup.TryGetManualLink(gameId, out var link) &&
                string.Equals(link?.SourceKey, "Steam", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(link.SourceGameId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                appId = parsed.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Accepts a store search only when exactly one result has the game's name. When none does,
        /// searches again with the loose name ("Deus Ex - Mankind Divided" as "deus ex mankind
        /// divided"), since the store finds nothing for some punctuated titles and names editions
        /// differently. Either way the link is marked found by name, so the overlay confirms it
        /// against the achievements.
        /// </summary>
        private async Task<MetadataSourceLink> SearchByNameAsync(string name, CancellationToken ct)
        {
            var normalized = GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(name));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var exact = await SearchAsync(
                name,
                result => string.Equals(
                    GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(result)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase),
                ct).ConfigureAwait(false);
            if (exact.Count == 1)
            {
                return new MetadataSourceLink(exact[0].SourceGameId, exact[0].Name, foundByName: true);
            }

            var loose = GameNameNormalizer.ToLooseKey(name);
            if (exact.Count > 1 || loose.Length == 0)
            {
                _logger?.Debug($"[SteamHuntersMetadata] Name search for '{name}' found {exact.Count} exact matches; no link.");
                return null;
            }

            var close = await SearchAsync(
                loose,
                result => string.Equals(GameNameNormalizer.ToLooseKey(result), loose, StringComparison.Ordinal),
                ct).ConfigureAwait(false);
            if (close.Count != 1)
            {
                _logger?.Debug($"[SteamHuntersMetadata] Name search for '{name}' found {close.Count} close matches; no link.");
                return null;
            }

            return new MetadataSourceLink(close[0].SourceGameId, close[0].Name, foundByName: true);
        }

        private async Task<List<ManualGameSearchResult>> SearchAsync(string query, Func<string, bool> accepts, CancellationToken ct)
        {
            var results = await SteamSchema.SearchGamesAsync(query, Language, ct).ConfigureAwait(false);
            return (results ?? new List<ManualGameSearchResult>())
                .Where(result => result != null &&
                                 !string.IsNullOrWhiteSpace(result.SourceGameId) &&
                                 accepts(result.Name))
                .GroupBy(result => result.SourceGameId)
                .Select(group => group.First())
                .ToList();
        }
    }
}
