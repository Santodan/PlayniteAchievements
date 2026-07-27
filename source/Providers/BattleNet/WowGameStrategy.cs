using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.BattleNet.Models;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class WowGameStrategy
    {
        private readonly BattleNetApiClient _client;
        private readonly BattleNetSessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly BattleNetWowCatalogCache _catalogCache;

        public WowGameStrategy(BattleNetApiClient client, BattleNetSessionManager sessionManager, ILogger logger, string pluginUserDataPath = null)
        {
            _client = client;
            _sessionManager = sessionManager;
            _logger = logger;
            _catalogCache = new BattleNetWowCatalogCache(pluginUserDataPath, logger);
        }

        public bool MatchesGame(Game game)
        {
            return BattleNetGameSupport.IsWowGame(game);
        }

        public async Task<GameAchievementData> FetchAchievementsAsync(Game game, string locale, CancellationToken ct)
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            var region = settings.WowRegion;
            var realmSlug = settings.WowRealmSlug;
            var character = settings.WowCharacter;
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;
            var apiLocale = BattleNetLocaleMapper.ToApiLocale(effectiveLocale);

            if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(realmSlug) || string.IsNullOrEmpty(character))
            {
                _logger?.Warn($"[BattleNet/WoW] Region, realm, or character not configured. region={Presence(region)}, realmSlug={Presence(realmSlug)}, character={Presence(character)}");
                return null;
            }

            var wowLocale = BattleNetLocaleMapper.ToWowWebLocale(effectiveLocale);

            if (!BattleNetGameSupport.HasApiCredentials(settings))
            {
                var publicAchievements = await FetchPublicCatalogAchievementsAsync(region, realmSlug, character, wowLocale, ct).ConfigureAwait(false);
                _logger?.Debug("[BattleNet/WoW] Battle.net API credentials are not configured; using public WoW achievement data only.");
                await EnrichDataForAzerothRarityAsync(publicAchievements, settings, ct).ConfigureAwait(false);
                return CreateDataOrNull(game, publicAchievements);
            }

            var catalogToken = default(string);
            try
            {
                catalogToken = await _client.GetClientCredentialsAccessTokenAsync(
                    region,
                    settings.BattleNetClientId,
                    settings.BattleNetClientSecret,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, "[BattleNet/WoW] Could not obtain Battle.net API token; using public WoW achievement data only.");
                var publicAchievements = await FetchPublicCatalogAchievementsAsync(region, realmSlug, character, wowLocale, ct).ConfigureAwait(false);
                await EnrichDataForAzerothRarityAsync(publicAchievements, settings, ct).ConfigureAwait(false);
                return CreateDataOrNull(game, publicAchievements);
            }

            var catalog = new List<WowOfficialAchievementDefinition>();
            var catalogSignature = default(string);
            try
            {
                catalog = await _client.GetWowOfficialAchievementCatalogAsync(
                    region,
                    apiLocale,
                    catalogToken,
                    ct).ConfigureAwait(false);
                catalogSignature = BattleNetWowCatalogCache.BuildOfficialIndexSignature(catalog);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, "[BattleNet/WoW] Could not load official achievement index; using cached or public catalog data only.");
            }

            var guildCategoryIds = new HashSet<int>();
            try
            {
                guildCategoryIds = await _client.GetWowGuildCategoryIdsAsync(
                    region,
                    apiLocale,
                    catalogToken,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, "[BattleNet/WoW] Could not load guild achievement categories; guild achievements may not be excluded.");
            }

            var achievements = await LoadOrBuildFullCatalogAsync(
                region,
                realmSlug,
                character,
                apiLocale,
                wowLocale,
                catalogSignature,
                catalog,
                catalogToken,
                guildCategoryIds,
                ct).ConfigureAwait(false);

            var mergeStats = await MergeOfficialAchievementDataAsync(
                achievements,
                settings,
                effectiveLocale,
                guildCategoryIds,
                ct).ConfigureAwait(false);
            if (mergeStats.FetchedCharacters > 0)
            {
                _logger?.Info($"[BattleNet/WoW] Official achievement merge completed. characters={mergeStats.FetchedCharacters}, completions={mergeStats.CompletionCount}, updated={mergeStats.UpdatedCount}, added={mergeStats.AddedCount}, mode={mergeStats.Mode}");
            }

            if (achievements.Count == 0)
            {
                _logger?.Warn($"[BattleNet/WoW] No WoW achievements were parsed from public data or official index. game={GameLabel(game)}");
                return null;
            }

            await EnrichDataForAzerothRarityAsync(achievements, settings, ct).ConfigureAwait(false);

            return CreateDataOrNull(game, achievements);
        }

        private async Task<List<AchievementDetail>> LoadOrBuildFullCatalogAsync(
            string region,
            string realmSlug,
            string character,
            string apiLocale,
            string wowLocale,
            string catalogSignature,
            List<WowOfficialAchievementDefinition> catalog,
            string bearerToken,
            HashSet<int> guildCategoryIds,
            CancellationToken ct)
        {
            var catalogCount = catalog?.Count ?? 0;
            if (_catalogCache.TryLoad(
                region,
                apiLocale,
                wowLocale,
                catalogSignature,
                catalogCount,
                out var cachedAchievements,
                out var cacheReason))
            {
                _logger?.Info($"[BattleNet/WoW] Loaded WoW catalog metadata from cache. achievements={cachedAchievements.Count}, scope={region}/{apiLocale}/{wowLocale}");
                return cachedAchievements;
            }

            if (_catalogCache.IsEnabled)
            {
                _logger?.Debug($"[BattleNet/WoW] WoW catalog cache miss. reason={cacheReason ?? "unknown"}, scope={region}/{apiLocale}/{wowLocale}");
            }

            var achievements = await FetchPublicCatalogAchievementsAsync(region, realmSlug, character, wowLocale, ct).ConfigureAwait(false);

            var addedFromIndex = MergeOfficialCatalogIndex(achievements, catalog, guildCategoryIds);
            if (addedFromIndex > 0)
            {
                _logger?.Info($"[BattleNet/WoW] Added {addedFromIndex} achievements from the official achievement index.");
            }

            await EnrichCatalogAchievementsAsync(achievements, region, apiLocale, bearerToken, ct).ConfigureAwait(false);

            _catalogCache.Save(
                region,
                apiLocale,
                wowLocale,
                catalogSignature,
                catalogCount,
                achievements);

            return achievements;
        }

        private async Task<int> EnrichCatalogAchievementsAsync(
            List<AchievementDetail> achievements,
            string region,
            string apiLocale,
            string bearerToken,
            CancellationToken ct)
        {
            if (achievements == null ||
                achievements.Count == 0 ||
                string.IsNullOrWhiteSpace(bearerToken))
            {
                return 0;
            }

            var pending = achievements
                .Where(item => item != null &&
                    string.IsNullOrWhiteSpace(item.UnlockedIconPath) &&
                    TryParseAchievementId(item.ApiName) > 0)
                .ToList();
            if (pending.Count == 0)
            {
                return 0;
            }

            _logger?.Info($"[BattleNet/WoW] Enriching {pending.Count} catalog achievements missing icons/metadata. This may take a while on first refresh.");

            var definitionCache = new Dictionary<string, WowOfficialAchievementDefinition>(StringComparer.Ordinal);
            var mediaCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var enriched = 0;

            foreach (var detail in pending)
            {
                ct.ThrowIfCancellationRequested();

                var id = TryParseAchievementId(detail.ApiName);
                if (id <= 0)
                {
                    continue;
                }

                var definition = default(WowOfficialAchievementDefinition);
                var definitionCacheKey = $"{region}:{id.ToString(CultureInfo.InvariantCulture)}:{apiLocale}";
                if (!definitionCache.TryGetValue(definitionCacheKey, out definition))
                {
                    try
                    {
                        definition = await _client.GetWowOfficialAchievementDefinitionByIdAsync(
                            region,
                            id,
                            apiLocale,
                            bearerToken,
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _logger?.Debug(ex, $"[BattleNet/WoW] Failed to fetch achievement definition for id={id} during enrichment.");
                    }

                    definitionCache[definitionCacheKey] = definition;
                }

                var iconUrl = await TryGetOfficialAchievementIconAsync(
                    definition?.Media?.Href,
                    bearerToken,
                    mediaCache,
                    id,
                    ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(iconUrl))
                {
                    var fallbackMediaHref = BuildOfficialAchievementMediaHref(definition?.Media?.Href, region, id);
                    if (!string.IsNullOrWhiteSpace(fallbackMediaHref) &&
                        !string.Equals(definition?.Media?.Href, fallbackMediaHref, StringComparison.OrdinalIgnoreCase))
                    {
                        iconUrl = await TryGetOfficialAchievementIconAsync(
                            fallbackMediaHref,
                            bearerToken,
                            mediaCache,
                            id,
                            ct).ConfigureAwait(false);
                    }
                }

                if (string.IsNullOrWhiteSpace(detail.UnlockedIconPath) &&
                    !string.IsNullOrWhiteSpace(iconUrl))
                {
                    detail.UnlockedIconPath = iconUrl;
                    enriched++;
                }

                if (definition != null)
                {
                    MergeOfficialDefinitionFields(detail, definition, enrichIcon: true, iconUrl: iconUrl);
                }
            }

            _logger?.Info($"[BattleNet/WoW] Enrichment resolved icons for {enriched}/{pending.Count} achievements.");
            return enriched;
        }

        private static int TryParseAchievementId(string apiName)
        {
            return int.TryParse(
                (apiName ?? string.Empty).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var id)
                ? id
                : 0;
        }

        private async Task<List<AchievementDetail>> FetchPublicCatalogAchievementsAsync(
            string region,
            string realmSlug,
            string character,
            string wowLocale,
            CancellationToken ct)
        {
            var allData = await _client.GetWowAllAchievementsAsync(region, realmSlug, character, wowLocale, ct).ConfigureAwait(false);
            return ParseAchievements(allData);
        }

        private static GameAchievementData CreateDataOrNull(Game game, List<AchievementDetail> achievements)
        {
            if (achievements == null || achievements.Count == 0)
            {
                return null;
            }

            return new GameAchievementData
            {
                ProviderKey = "BattleNet",
                GameName = game.Name,
                PlayniteGameId = game.Id,
                AppId = StableAppId("WoW"),
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements.Count > 0
            };
        }

        internal static List<AchievementDetail> ParseAchievements(IEnumerable<WowAchievementsData> allData)
        {
            var achievements = new List<AchievementDetail>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var categoryData in allData ?? Enumerable.Empty<WowAchievementsData>())
            {
                if (categoryData == null)
                {
                    continue;
                }

                var category = categoryData.Name ?? categoryData.Category;
                var subcategories = ReadSubcategories(categoryData.Subcategories);
                if (subcategories.Count > 0)
                {
                    foreach (var subcategory in subcategories)
                    {
                        AddAchievements(achievements, seen, subcategory?.Achievements, category);
                    }
                }
                else
                {
                    AddAchievements(achievements, seen, categoryData.AchievementsList, category);
                }
            }

            return achievements;
        }

        private static int MergeOfficialCatalogIndex(
            List<AchievementDetail> achievements,
            IEnumerable<WowOfficialAchievementDefinition> catalog,
            HashSet<int> guildCategoryIds)
        {
            if (achievements == null)
            {
                return 0;
            }

            var byId = achievements
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var added = 0;
            foreach (var definition in catalog ?? Enumerable.Empty<WowOfficialAchievementDefinition>())
            {
                var id = definition?.Id ?? 0;
                var key = id.ToString(CultureInfo.InvariantCulture);
                if (id <= 0)
                {
                    continue;
                }

                if (IsGuildCategory(definition?.Category, guildCategoryIds))
                {
                    if (byId.TryGetValue(key, out var existingGuildAchievement))
                    {
                        achievements.Remove(existingGuildAchievement);
                        byId.Remove(key);
                    }

                    continue;
                }

                if (byId.TryGetValue(key, out var existing))
                {
                    MergeOfficialDefinitionFields(existing, definition, enrichIcon: false, iconUrl: null);
                    continue;
                }

                var detail = new AchievementDetail
                {
                    ApiName = key,
                    DisplayName = FirstNonEmpty(definition?.Name, key),
                    Description = definition?.Description,
                    Points = definition != null && definition.Points > 0 ? definition.Points : (int?)null,
                    Category = definition?.Category?.Name,
                    CategoryType = IsOfficiallyUnobtainable(definition) ? "Missable" : null,
                    Hidden = definition?.IsHidden == true,
                    ProviderKey = "BattleNet",
                    Unlocked = false
                };
                achievements.Add(detail);
                byId[key] = detail;
                added++;
            }

            return added;
        }

        private static void MergeOfficialDefinitionFields(
            AchievementDetail detail,
            WowOfficialAchievementDefinition definition,
            bool enrichIcon,
            string iconUrl)
        {
            if (detail == null || definition == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(detail.DisplayName) ||
                string.Equals(detail.DisplayName, detail.ApiName, StringComparison.Ordinal))
            {
                detail.DisplayName = FirstNonEmpty(definition.Name, detail.DisplayName);
            }

            if (string.IsNullOrWhiteSpace(detail.Description))
            {
                detail.Description = definition.Description;
            }

            if (!detail.Points.HasValue && definition.Points > 0)
            {
                detail.Points = definition.Points;
            }

            if (string.IsNullOrWhiteSpace(detail.Category))
            {
                detail.Category = FirstNonEmpty(definition.Category?.Name, detail.Category);
            }

            if (IsOfficiallyUnobtainable(definition))
            {
                detail.CategoryType = "Missable";
            }

            if (definition.IsHidden)
            {
                detail.Hidden = true;
            }

            if (enrichIcon && string.IsNullOrWhiteSpace(detail.UnlockedIconPath))
            {
                detail.UnlockedIconPath = iconUrl;
            }
        }

        internal async Task<WowOfficialMergeStats> MergeOfficialAchievementDataAsync(
            List<AchievementDetail> achievements,
            BattleNetSettings settings,
            string locale,
            HashSet<int> guildCategoryIds,
            CancellationToken ct)
        {
            var stats = new WowOfficialMergeStats();
            if (achievements == null || settings == null)
            {
                return stats;
            }

            if (string.IsNullOrWhiteSpace(settings.WowRegion))
            {
                return stats;
            }

            var apiLocale = BattleNetLocaleMapper.ToApiLocale(locale);
            var region = settings.WowRegion;
            var bearerToken = default(string);
            var characters = new List<WowOfficialCharacterTarget>();

            if (settings.WowAggregateAccountCharacters && _sessionManager?.IsAuthenticated == true)
            {
                try
                {
                    bearerToken = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
                    var accountProfile = await _client.GetWowAccountProfileAsync(region, bearerToken, apiLocale, ct).ConfigureAwait(false);
                    characters = ExtractAccountCharacters(region, accountProfile);
                    stats.Mode = "account";
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Warn(ex, "[BattleNet/WoW] Account character aggregation failed. Falling back to configured character if API credentials are available.");
                    bearerToken = null;
                    characters.Clear();
                }
            }

            if (characters.Count == 0)
            {
                var configured = BuildConfiguredCharacterTarget(settings);
                if (configured == null)
                {
                    return stats;
                }

                characters.Add(configured);
                stats.Mode = "configured-character";

                if (string.IsNullOrWhiteSpace(bearerToken))
                {
                    if (!BattleNetGameSupport.HasApiCredentials(settings))
                    {
                        _logger?.Debug("[BattleNet/WoW] Battle.net API credentials are not configured; skipping official completion merge.");
                        return stats;
                    }

                    try
                    {
                        bearerToken = await _client.GetClientCredentialsAccessTokenAsync(
                            region,
                            settings.BattleNetClientId,
                            settings.BattleNetClientSecret,
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _logger?.Warn(ex, "[BattleNet/WoW] Could not obtain Battle.net API token; skipping official completion merge.");
                        return stats;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                return stats;
            }

            var byId = achievements
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var definitionCache = new Dictionary<string, WowOfficialAchievementDefinition>(StringComparer.Ordinal);
            var mediaCache = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var character in DeduplicateCharacters(characters))
            {
                ct.ThrowIfCancellationRequested();

                WowCharacterAchievementsResponse response;
                try
                {
                    response = await _client.GetWowOfficialCharacterAchievementsAsync(
                        character.Region,
                        character.RealmSlug,
                        character.CharacterName,
                        apiLocale,
                        bearerToken,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Debug(ex, $"[BattleNet/WoW] Official character achievement fetch failed for {character.SourceLabel}.");
                    continue;
                }

                stats.FetchedCharacters++;
                foreach (var progress in response?.Achievements ?? Enumerable.Empty<WowOfficialAchievementProgress>())
                {
                    ct.ThrowIfCancellationRequested();

                    var id = progress?.AchievementId ?? 0;
                    if (id <= 0 || !progress.CompletedTimestamp.HasValue)
                    {
                        continue;
                    }

                    stats.CompletionCount++;
                    var key = id.ToString(CultureInfo.InvariantCulture);
                    if (!byId.TryGetValue(key, out var detail))
                    {
                        detail = await CreateMissingOfficialAchievementAsync(
                            progress,
                            character.Region,
                            apiLocale,
                            bearerToken,
                            definitionCache,
                            mediaCache,
                            guildCategoryIds,
                            ct).ConfigureAwait(false);
                        if (detail == null)
                        {
                            continue;
                        }

                        achievements.Add(detail);
                        byId[key] = detail;
                        stats.AddedCount++;
                    }

                    if (ApplyOfficialCompletion(detail, progress.CompletedTimestamp.Value))
                    {
                        stats.UpdatedCount++;
                    }
                }
            }

            return stats;
        }

        private async Task EnrichDataForAzerothRarityAsync(
            IList<AchievementDetail> achievements,
            BattleNetSettings settings,
            CancellationToken ct)
        {
            if (settings?.UseDataForAzerothForWowRarity != true ||
                achievements == null ||
                achievements.Count == 0)
            {
                return;
            }

            try
            {
                var rarity = await _client.GetDataForAzerothWowAchievementRarityAsync(ct).ConfigureAwait(false);
                var updated = ApplyDataForAzerothRarity(achievements, rarity);
                _logger?.Info($"[BattleNet/WoW] Applied Data for Azeroth rarity to {updated}/{achievements.Count} achievements.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[BattleNet/WoW] Data for Azeroth rarity enrichment failed. Continuing without WoW global rarity percentages.");
            }
        }

        internal static int ApplyDataForAzerothRarity(
            IList<AchievementDetail> achievements,
            IDictionary<string, double> rarityPercentByAchievementId)
        {
            if (achievements == null || rarityPercentByAchievementId == null || rarityPercentByAchievementId.Count == 0)
            {
                return 0;
            }

            var updated = 0;
            var valuesAreRatios = ShouldTreatRarityValuesAsRatios(rarityPercentByAchievementId.Values);
            foreach (var achievement in achievements)
            {
                var apiName = NormalizeApiName(achievement?.ApiName);
                if (string.IsNullOrWhiteSpace(apiName) ||
                    !rarityPercentByAchievementId.TryGetValue(apiName, out var percent))
                {
                    continue;
                }

                var normalized = NormalizePercent(percent, valuesAreRatios);
                if (!normalized.HasValue)
                {
                    continue;
                }

                var rarity = PercentRarityHelper.GetRarityTier(normalized.Value);
                if (achievement.GlobalPercentUnlocked == normalized.Value &&
                    achievement.Rarity == rarity)
                {
                    continue;
                }

                achievement.GlobalPercentUnlocked = normalized.Value;
                achievement.Rarity = rarity;
                updated++;
            }

            return updated;
        }

        private async Task<AchievementDetail> CreateMissingOfficialAchievementAsync(
            WowOfficialAchievementProgress progress,
            string region,
            string locale,
            string bearerToken,
            Dictionary<string, WowOfficialAchievementDefinition> definitionCache,
            Dictionary<string, string> mediaCache,
            HashSet<int> guildCategoryIds,
            CancellationToken ct)
        {
            var id = progress?.AchievementId ?? 0;
            if (id <= 0)
            {
                return null;
            }

            var definitionHref = progress.Achievement?.Key?.Href;
            var definition = default(WowOfficialAchievementDefinition);
            var definitionCacheKey = FirstNonEmpty(
                definitionHref,
                $"{region}:{id.ToString(CultureInfo.InvariantCulture)}:{locale}");
            if (!definitionCache.TryGetValue(definitionCacheKey, out definition))
            {
                try
                {
                    definition = !string.IsNullOrWhiteSpace(definitionHref)
                        ? await _client.GetWowOfficialAchievementDefinitionAsync(
                            definitionHref,
                            locale,
                            bearerToken,
                            ct).ConfigureAwait(false)
                        : await _client.GetWowOfficialAchievementDefinitionByIdAsync(
                            region,
                            id,
                            locale,
                            bearerToken,
                            ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Debug(ex, $"[BattleNet/WoW] Failed to fetch achievement definition for id={id}.");
                }

                definitionCache[definitionCacheKey] = definition;
            }

            if (IsGuildCategory(definition?.Category, guildCategoryIds))
            {
                return null;
            }

            var iconUrl = default(string);
            var mediaHref = definition?.Media?.Href;
            if (!string.IsNullOrWhiteSpace(mediaHref))
            {
                iconUrl = await TryGetOfficialAchievementIconAsync(
                    mediaHref,
                    bearerToken,
                    mediaCache,
                    id,
                    ct).ConfigureAwait(false);
            }

            var fallbackMediaHref = BuildOfficialAchievementMediaHref(definitionHref, region, id);
            if (string.IsNullOrWhiteSpace(iconUrl) &&
                !string.IsNullOrWhiteSpace(fallbackMediaHref) &&
                !string.Equals(mediaHref, fallbackMediaHref, StringComparison.OrdinalIgnoreCase))
            {
                iconUrl = await TryGetOfficialAchievementIconAsync(
                    fallbackMediaHref,
                    bearerToken,
                    mediaCache,
                    id,
                    ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                _logger?.Debug($"[BattleNet/WoW] No official achievement icon resolved for id={id}.");
            }

            return new AchievementDetail
            {
                ApiName = id.ToString(CultureInfo.InvariantCulture),
                DisplayName = FirstNonEmpty(definition?.Name, progress.Achievement?.Name, id.ToString(CultureInfo.InvariantCulture)),
                Description = definition?.Description,
                UnlockedIconPath = iconUrl,
                Points = definition != null && definition.Points > 0 ? definition.Points : (int?)null,
                Category = definition?.Category?.Name,
                CategoryType = IsOfficiallyUnobtainable(definition) ? "Missable" : null,
                Hidden = definition?.IsHidden == true,
                ProviderKey = "BattleNet",
                Unlocked = false
            };
        }

        private async Task<string> TryGetOfficialAchievementIconAsync(
            string mediaHref,
            string bearerToken,
            Dictionary<string, string> mediaCache,
            int achievementId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mediaHref))
            {
                return null;
            }

            if (mediaCache.TryGetValue(mediaHref, out var iconUrl))
            {
                return iconUrl;
            }

            try
            {
                iconUrl = (await _client.GetWowOfficialAchievementMediaAsync(
                    mediaHref,
                    bearerToken,
                    ct).ConfigureAwait(false))?.GetIconUrl();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Debug(ex, $"[BattleNet/WoW] Failed to fetch achievement media for id={achievementId}.");
            }

            mediaCache[mediaHref] = iconUrl;
            return iconUrl;
        }

        internal static string BuildOfficialAchievementMediaHref(string definitionHref, string region, int achievementId)
        {
            if (achievementId <= 0)
            {
                return null;
            }

            var normalizedRegion = NormalizeOfficialRegion(region);
            var authority = $"https://{normalizedRegion}.api.blizzard.com";
            var namespaceValue = $"static-{normalizedRegion}";

            if (Uri.TryCreate(definitionHref, UriKind.Absolute, out var uri))
            {
                authority = uri.GetLeftPart(UriPartial.Authority);
                namespaceValue = FirstNonEmpty(GetQueryValue(uri.Query, "namespace"), namespaceValue);
            }

            return $"{authority}/data/wow/media/achievement/{achievementId}?namespace={Uri.EscapeDataString(namespaceValue)}";
        }

        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var normalized = query[0] == '?' ? query.Substring(1) : query;
            foreach (var part in normalized.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(new[] { '=' }, 2);
                var name = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pieces.Length > 1
                        ? Uri.UnescapeDataString(pieces[1].Replace("+", " "))
                        : string.Empty;
                }
            }

            return null;
        }

        private static string NormalizeOfficialRegion(string region)
        {
            var value = string.IsNullOrWhiteSpace(region) ? "us" : region.Trim().ToLowerInvariant();
            switch (value)
            {
                case "eu":
                case "kr":
                case "tw":
                case "cn":
                case "us":
                    return value;
                default:
                    return "us";
            }
        }

        private static bool IsOfficiallyUnobtainable(WowOfficialAchievementDefinition definition)
        {
            return definition?.IsObtainable == false ||
                definition?.IsObtainableInGame == false;
        }

        private static bool ApplyOfficialCompletion(AchievementDetail detail, long completedTimestamp)
        {
            if (detail == null || completedTimestamp <= 0)
            {
                return false;
            }

            var unlockUtc = DateTimeOffset.FromUnixTimeMilliseconds(completedTimestamp).UtcDateTime;
            var changed = !detail.Unlocked ||
                !detail.UnlockTimeUtc.HasValue ||
                unlockUtc < detail.UnlockTimeUtc.Value;

            detail.Unlocked = true;
            if (!detail.UnlockTimeUtc.HasValue || unlockUtc < detail.UnlockTimeUtc.Value)
            {
                detail.UnlockTimeUtc = unlockUtc;
            }

            return changed;
        }

        private static WowOfficialCharacterTarget BuildConfiguredCharacterTarget(BattleNetSettings settings)
        {
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.WowRegion) ||
                string.IsNullOrWhiteSpace(settings.WowRealmSlug) ||
                string.IsNullOrWhiteSpace(settings.WowCharacter))
            {
                return null;
            }

            return new WowOfficialCharacterTarget
            {
                Region = settings.WowRegion,
                RealmSlug = settings.WowRealmSlug,
                CharacterName = settings.WowCharacter,
                SourceLabel = $"{settings.WowRegion}/{settings.WowRealmSlug}/{settings.WowCharacter}"
            };
        }

        private static List<WowOfficialCharacterTarget> ExtractAccountCharacters(
            string region,
            WowAccountProfileResponse accountProfile)
        {
            var characters = new List<WowOfficialCharacterTarget>();
            foreach (var account in accountProfile?.WowAccounts ?? Enumerable.Empty<WowAccountProfileAccount>())
            {
                foreach (var entry in account?.Characters ?? Enumerable.Empty<WowAccountProfileCharacterEntry>())
                {
                    var character = entry?.Character;
                    if (character == null ||
                        string.IsNullOrWhiteSpace(character.Name) ||
                        string.IsNullOrWhiteSpace(character.Realm?.Slug))
                    {
                        continue;
                    }

                    characters.Add(new WowOfficialCharacterTarget
                    {
                        Region = region,
                        RealmSlug = character.Realm.Slug,
                        CharacterName = character.Name,
                        SourceLabel = $"{region}/{character.Realm.Slug}/{character.Name}"
                    });
                }
            }

            return characters;
        }

        private static IEnumerable<WowOfficialCharacterTarget> DeduplicateCharacters(IEnumerable<WowOfficialCharacterTarget> characters)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var character in characters ?? Enumerable.Empty<WowOfficialCharacterTarget>())
            {
                if (character == null ||
                    string.IsNullOrWhiteSpace(character.Region) ||
                    string.IsNullOrWhiteSpace(character.RealmSlug) ||
                    string.IsNullOrWhiteSpace(character.CharacterName))
                {
                    continue;
                }

                var key = $"{character.Region}/{character.RealmSlug}/{character.CharacterName}";
                if (seen.Add(key))
                {
                    yield return character;
                }
            }
        }

        private static void AddAchievements(
            List<AchievementDetail> target,
            HashSet<string> seen,
            IEnumerable<WowAchievement> source,
            string category)
        {
            foreach (var achievement in source ?? Enumerable.Empty<WowAchievement>())
            {
                if (achievement == null || achievement.Id == 0)
                {
                    continue;
                }

                if (!seen.Add(achievement.Id.ToString()))
                {
                    continue;
                }

                var detail = new AchievementDetail
                {
                    ApiName = achievement.Id.ToString(CultureInfo.InvariantCulture),
                    DisplayName = achievement.Name,
                    Description = achievement.Description,
                    UnlockedIconPath = achievement.Icon?.Url,
                    Points = achievement.Point > 0 ? achievement.Point : (int?)null,
                    Category = category,
                    ProviderKey = "BattleNet",
                    Unlocked = achievement.Time.HasValue && achievement.Time.Value != default
                };

                if (detail.Unlocked)
                {
                    var time = achievement.Time.Value;
                    detail.UnlockTimeUtc = time.Kind == DateTimeKind.Utc
                        ? time
                        : time.Kind == DateTimeKind.Local
                            ? time.ToUniversalTime()
                            : DateTime.SpecifyKind(time, DateTimeKind.Utc);
                }

                target.Add(detail);
            }
        }

        internal static bool IsGuildCategory(
            WowOfficialAchievementCategory category,
            HashSet<int> guildCategoryIds)
        {
            return category != null &&
                guildCategoryIds != null &&
                category.Id > 0 &&
                guildCategoryIds.Contains(category.Id);
        }

        private static string NormalizeApiName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool ShouldTreatRarityValuesAsRatios(IEnumerable<double> values)
        {
            var sawUsableValue = false;
            foreach (var value in values ?? Enumerable.Empty<double>())
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                {
                    continue;
                }

                sawUsableValue = true;
                if (value > 1)
                {
                    return false;
                }
            }

            return sawUsableValue;
        }

        private static double? NormalizePercent(double value, bool valueIsRatio = false)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (valueIsRatio && value > 0 && value <= 1)
            {
                value *= 100.0;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static List<WowSubcategory> ReadSubcategories(object value)
        {
            try
            {
                if (value is JObject keyed)
                {
                    return keyed.Properties()
                        .Select(property => property.Value?.ToObject<WowSubcategory>())
                        .Where(item => item != null)
                        .ToList();
                }

                if (value is JArray array)
                {
                    return array
                        .Select(token => token?.ToObject<WowSubcategory>())
                        .Where(item => item != null)
                        .ToList();
                }

                return (value as IEnumerable<WowSubcategory>)?
                    .Where(item => item != null)
                    .ToList() ?? new List<WowSubcategory>();
            }
            catch
            {
                return new List<WowSubcategory>();
            }
        }

        private static int StableAppId(string id)
        {
            int hash = 0;
            foreach (char c in id)
            {
                hash = (hash << 5) - hash + c;
            }
            return Math.Abs(hash);
        }

        private static string Presence(string value) => string.IsNullOrWhiteSpace(value) ? "missing" : "set";

        private static string GameLabel(Game game)
        {
            if (game == null)
            {
                return "<null>";
            }

            return $"{game.Name ?? "<unnamed>"} ({game.Id})";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private sealed class WowOfficialCharacterTarget
        {
            public string Region { get; set; }
            public string RealmSlug { get; set; }
            public string CharacterName { get; set; }
            public string SourceLabel { get; set; }
        }

        internal sealed class WowOfficialMergeStats
        {
            public string Mode { get; set; } = "none";
            public int FetchedCharacters { get; set; }
            public int CompletionCount { get; set; }
            public int UpdatedCount { get; set; }
            public int AddedCount { get; set; }
        }
    }
}
