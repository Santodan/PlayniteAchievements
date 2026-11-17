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
using PlayniteAchievements.Services.Achievements;

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

        public Task<GameAchievementData> FetchAchievementsAsync(Game game, string locale, CancellationToken ct)
        {
            return FetchAchievementsAsync(game, locale, WowVersion.Retail, ct);
        }

        public async Task<GameAchievementData> FetchAchievementsAsync(Game game, string locale, WowVersion version, CancellationToken ct)
        {
            version = version ?? WowVersion.Retail;
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            if (!version.IsRetail)
            {
                return await FetchOfficialVersionAsync(game, locale, version, settings, ct).ConfigureAwait(false);
            }

            var region = settings.WowRegion;
            var realmSlug = settings.WowRealmSlug;
            var character = settings.WowCharacter;
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;
            var apiLocale = BattleNetLocaleMapper.ToApiLocale(effectiveLocale);

            if (!string.IsNullOrEmpty(region) && UsesAccount(settings, WowVersion.Retail))
            {
                // The catalog pages come from the account's highest-level character; the typed
                // character stays the fallback while not signed in.
                var accountCharacter = await FindAccountCatalogCharacterAsync(settings, region, apiLocale, ct).ConfigureAwait(false);
                if (accountCharacter != null)
                {
                    realmSlug = accountCharacter.RealmSlug;
                    character = accountCharacter.CharacterName;
                }
            }

            if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(realmSlug) || string.IsNullOrEmpty(character))
            {
                _logger?.Warn($"[BattleNet/WoW] Not signed in, and region, realm, or character not configured. region={Presence(region)}, realmSlug={Presence(realmSlug)}, character={Presence(character)}");
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

        /// <summary>
        /// The signed-in account's highest-level retail character with a realm and name, or null when
        /// not signed in or the account has none.
        /// </summary>
        private async Task<WowOfficialCharacterTarget> FindAccountCatalogCharacterAsync(
            BattleNetSettings settings,
            string region,
            string apiLocale,
            CancellationToken ct)
        {
            if (_sessionManager == null || !BattleNetGameSupport.HasOAuthSession(settings))
            {
                return null;
            }

            try
            {
                var token = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
                var profile = await _client.GetWowAccountProfileAsync(region, token, apiLocale, ct).ConfigureAwait(false);
                return ExtractAccountCharacters(region, profile)
                    .Where(c => c.HasRealmAndName)
                    .OrderByDescending(c => c.Level)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, "[BattleNet/WoW] Could not read the signed-in account's characters.");
                return null;
            }
        }

        /// <summary>
        /// Classic and Forever: the catalog comes from the version's official achievement categories,
        /// enriched with descriptions, points and icons, and cached per version; completions come from
        /// the account's characters or the configured character in that version.
        /// </summary>
        private async Task<GameAchievementData> FetchOfficialVersionAsync(
            Game game,
            string locale,
            WowVersion version,
            BattleNetSettings settings,
            CancellationToken ct)
        {
            var region = settings?.WowRegion;
            if (string.IsNullOrWhiteSpace(region))
            {
                _logger?.Warn($"{version.LogTag} Region not configured.");
                return null;
            }

            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;
            var apiLocale = BattleNetLocaleMapper.ToApiLocale(effectiveLocale);
            var token = await GetCatalogTokenAsync(settings, region, version, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var index = new List<WowOfficialAchievementDefinition>();
            try
            {
                index = await _client.GetWowOfficialAchievementCatalogAsync(region, apiLocale, token, ct, version.NamespacePrefix).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, $"{version.LogTag} Could not load the official achievement index; using cached catalog data only.");
            }

            var signature = BattleNetWowCatalogCache.BuildOfficialIndexSignature(index);
            var cacheScope = version.NamespacePrefix + region;
            var guildCategoryIds = new HashSet<int>();
            try
            {
                // Needed on a cache hit too, so a completed guild achievement is not added back.
                guildCategoryIds.UnionWith(await _client.GetWowGuildCategoryIdsAsync(region, apiLocale, token, ct, version.NamespacePrefix).ConfigureAwait(false));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, $"{version.LogTag} Could not load guild achievement categories; guild achievements may not be excluded.");
            }

            List<AchievementDetail> achievements;
            if (_catalogCache.TryLoad(cacheScope, apiLocale, "api", signature, index.Count, out var cached, out var cacheReason))
            {
                _logger?.Info($"{version.LogTag} Loaded catalog metadata from cache. achievements={cached.Count}, scope={cacheScope}/{apiLocale}");
                achievements = cached;
            }
            else
            {
                _logger?.Debug($"{version.LogTag} Catalog cache miss. reason={cacheReason ?? "unknown"}, scope={cacheScope}/{apiLocale}");
                achievements = await BuildCategorizedCatalogAsync(region, apiLocale, token, version, guildCategoryIds, ct).ConfigureAwait(false);
                var addedFromIndex = MergeOfficialCatalogIndex(achievements, index, guildCategoryIds);
                if (addedFromIndex > 0)
                {
                    _logger?.Info($"{version.LogTag} Added {addedFromIndex} achievements from the official index that no category listed.");
                }

                await EnrichCatalogAchievementsAsync(achievements, region, apiLocale, token, ct, version).ConfigureAwait(false);
                if (achievements.Count > 0)
                {
                    _catalogCache.Save(cacheScope, apiLocale, "api", signature, index.Count, achievements);
                }
            }

            var mergeStats = await MergeOfficialAchievementDataAsync(
                achievements,
                settings,
                effectiveLocale,
                guildCategoryIds,
                ct,
                version).ConfigureAwait(false);
            _logger?.Info($"{version.LogTag} Official achievement merge completed. characters={mergeStats.FetchedCharacters}, completions={mergeStats.CompletionCount}, updated={mergeStats.UpdatedCount}, added={mergeStats.AddedCount}, mode={mergeStats.Mode}");

            if (achievements.Count == 0)
            {
                _logger?.Warn($"{version.LogTag} No achievements were parsed from the official API. game={GameLabel(game)}");
                return null;
            }

            await EnrichDataForAzerothRarityAsync(achievements, settings, ct, version).ConfigureAwait(false);

            return CreateDataOrNull(game, achievements, version);
        }

        /// <summary>
        /// A token for the version's static data: the app's client credentials when saved, otherwise
        /// the signed-in account's token.
        /// </summary>
        private async Task<string> GetCatalogTokenAsync(BattleNetSettings settings, string region, WowVersion version, CancellationToken ct)
        {
            try
            {
                if (BattleNetGameSupport.HasApiCredentials(settings))
                {
                    return await _client.GetClientCredentialsAccessTokenAsync(
                        region,
                        settings.BattleNetClientId,
                        settings.BattleNetClientSecret,
                        ct).ConfigureAwait(false);
                }

                if (_sessionManager != null && BattleNetGameSupport.HasOAuthSession(settings))
                {
                    return await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, $"{version.LogTag} Could not obtain a Battle.net API token.");
                return null;
            }

            _logger?.Warn($"{version.LogTag} Needs Battle.net API credentials or a signed-in account.");
            return null;
        }

        /// <summary>
        /// Builds the catalog from every achievement category in the version's namespace, so each
        /// achievement keeps its full category path. Guild categories, and categories under them,
        /// are skipped and collected into <paramref name="guildCategoryIds"/>.
        /// </summary>
        private async Task<List<AchievementDetail>> BuildCategorizedCatalogAsync(
            string region,
            string apiLocale,
            string token,
            WowVersion version,
            HashSet<int> guildCategoryIds,
            CancellationToken ct)
        {
            WowAchievementCategoryIndexResponse categoryIndex = null;
            try
            {
                categoryIndex = await _client.GetWowAchievementCategoryIndexAsync(region, apiLocale, token, ct, version.NamespacePrefix).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger?.Warn(ex, $"{version.LogTag} Could not load the achievement category index.");
            }

            var resources = new List<WowAchievementCategoryResource>();
            var references = (categoryIndex?.Categories ?? new List<WowAchievementCategoryReference>())
                .Concat(categoryIndex?.RootCategories ?? new List<WowAchievementCategoryReference>())
                .Concat(categoryIndex?.GuildCategories ?? new List<WowAchievementCategoryReference>())
                .Where(r => r != null && r.Id > 0)
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .ToList();
            foreach (var reference in references)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var resource = await _client.GetWowAchievementCategoryAsync(region, reference.Id, apiLocale, token, ct, version.NamespacePrefix).ConfigureAwait(false);
                    if (resource != null)
                    {
                        if (resource.Id <= 0)
                        {
                            resource.Id = reference.Id;
                        }

                        resource.Name = FirstNonEmpty(resource.Name, reference.Name);
                        resources.Add(resource);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Debug(ex, $"{version.LogTag} Failed to load achievement category id={reference.Id}.");
                }
            }

            foreach (var guild in categoryIndex?.GuildCategories ?? new List<WowAchievementCategoryReference>())
            {
                if (guild != null && guild.Id > 0)
                {
                    guildCategoryIds.Add(guild.Id);
                }
            }

            var achievements = BuildCatalogFromCategories(resources, guildCategoryIds);
            _logger?.Info($"{version.LogTag} Built catalog from categories. categories={resources.Count}, achievements={achievements.Count}, guildCategories={guildCategoryIds.Count}");
            return achievements;
        }

        /// <summary>
        /// Maps category resources to catalog rows. A category is a guild category when it is marked
        /// as one, is already in <paramref name="guildCategoryIds"/>, or sits under one; all of those
        /// are added to the set and their achievements left out.
        /// </summary>
        internal static List<AchievementDetail> BuildCatalogFromCategories(
            IEnumerable<WowAchievementCategoryResource> resources,
            HashSet<int> guildCategoryIds)
        {
            var byId = (resources ?? Enumerable.Empty<WowAchievementCategoryResource>())
                .Where(r => r != null && r.Id > 0)
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First());

            bool IsGuild(int id)
            {
                var visited = new HashSet<int>();
                while (id > 0 && visited.Add(id))
                {
                    if (guildCategoryIds.Contains(id) ||
                        (byId.TryGetValue(id, out var category) && category.IsGuildCategory))
                    {
                        return true;
                    }

                    id = byId.TryGetValue(id, out var current) ? current.ParentCategory?.Id ?? 0 : 0;
                }

                return false;
            }

            string PathOf(WowAchievementCategoryResource category)
            {
                var segments = new List<string>();
                var visited = new HashSet<int>();
                var current = category;
                while (current != null && visited.Add(current.Id))
                {
                    segments.Insert(0, current.Name);
                    var parentId = current.ParentCategory?.Id ?? 0;
                    if (parentId <= 0)
                    {
                        break;
                    }

                    if (!byId.TryGetValue(parentId, out var parent))
                    {
                        segments.Insert(0, current.ParentCategory.Name);
                        break;
                    }

                    current = parent;
                }

                return CategoryPathHelper.JoinRaw(segments.ToArray());
            }

            foreach (var id in byId.Keys.Where(IsGuild).ToList())
            {
                guildCategoryIds.Add(id);
            }

            var achievements = new List<AchievementDetail>();
            var seen = new HashSet<int>();
            foreach (var category in byId.Values.OrderBy(c => c.Id))
            {
                if (guildCategoryIds.Contains(category.Id))
                {
                    continue;
                }

                var path = PathOf(category);
                foreach (var reference in category.Achievements ?? new List<WowOfficialAchievementReference>())
                {
                    if (reference == null || reference.Id <= 0 || !seen.Add(reference.Id))
                    {
                        continue;
                    }

                    var key = reference.Id.ToString(CultureInfo.InvariantCulture);
                    achievements.Add(new AchievementDetail
                    {
                        ApiName = key,
                        DisplayName = FirstNonEmpty(reference.Name, key),
                        Description = reference.Description,
                        Points = reference.Points.HasValue && reference.Points.Value > 0 ? reference.Points : null,
                        Category = path,
                        Hidden = reference.IsHidden,
                        ProviderKey = "BattleNet",
                        Unlocked = false
                    });
                }
            }

            return achievements;
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
            CancellationToken ct,
            WowVersion version = null)
        {
            version = version ?? WowVersion.Retail;
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

            _logger?.Info($"{version.LogTag} Enriching {pending.Count} catalog achievements missing icons/metadata. This may take a while on first refresh.");

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
                            ct,
                            version.NamespacePrefix).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _logger?.Debug(ex, $"{version.LogTag} Failed to fetch achievement definition for id={id} during enrichment.");
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
                    var fallbackMediaHref = BuildOfficialAchievementMediaHref(definition?.Media?.Href, region, id, version.NamespacePrefix);
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

            _logger?.Info($"{version.LogTag} Enrichment resolved icons for {enriched}/{pending.Count} achievements.");
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

        private static GameAchievementData CreateDataOrNull(Game game, List<AchievementDetail> achievements, WowVersion version = null)
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
                AppId = StableAppId((version ?? WowVersion.Retail).AppIdSeed),
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
                    // The catalog nests achievements one level below the category ("Dungeons &
                    // Raids" > "Cataclysm"), which is how the in-game journal presents them.
                    // JoinRaw degenerates to the category alone when a subcategory is unnamed.
                    foreach (var subcategory in subcategories)
                    {
                        AddAchievements(
                            achievements,
                            seen,
                            subcategory?.Achievements,
                            CategoryPathHelper.JoinRaw(category, subcategory?.Name));
                    }
                }
                else
                {
                    AddAchievements(
                        achievements,
                        seen,
                        categoryData.AchievementsList,
                        CategoryPathHelper.JoinRaw(category));
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
                    // Flat by necessity: the official achievement-category resource exposes a
                    // parent_category that WowOfficialAchievementCategory does not model, so a
                    // catalog-only achievement lands at the root next to the nested catalog ones.
                    Category = CategoryPathHelper.JoinRaw(definition?.Category?.Name),
                    Tags = IsOfficiallyUnobtainable(definition) ? "Unobtainable" : null,
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
                detail.Tags = "Unobtainable";
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
            CancellationToken ct,
            WowVersion version = null)
        {
            version = version ?? WowVersion.Retail;
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

            if (_sessionManager != null && UsesAccount(settings, version) && BattleNetGameSupport.HasOAuthSession(settings))
            {
                try
                {
                    bearerToken = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
                    var accountProfile = await _client.GetWowAccountProfileAsync(region, bearerToken, apiLocale, ct, version.NamespacePrefix).ConfigureAwait(false);
                    characters = ExtractAccountCharacters(region, accountProfile);
                    stats.Mode = "account";
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Warn(ex, $"{version.LogTag} Account character aggregation failed. Falling back to configured character if API credentials are available.");
                    bearerToken = null;
                    characters.Clear();
                }
            }

            if (characters.Count == 0)
            {
                var configured = BuildConfiguredCharacterTarget(settings, version);
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
                        _logger?.Debug($"{version.LogTag} Battle.net API credentials are not configured; skipping official completion merge.");
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
                        _logger?.Warn(ex, $"{version.LogTag} Could not obtain Battle.net API token; skipping official completion merge.");
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
                    response = character.HasRealmAndName
                        ? await _client.GetWowOfficialCharacterAchievementsAsync(
                            character.Region,
                            character.RealmSlug,
                            character.CharacterName,
                            apiLocale,
                            bearerToken,
                            ct,
                            version.NamespacePrefix).ConfigureAwait(false)
                        : await _client.GetWowCharacterAchievementsByProfileHrefAsync(
                            character.ProfileHref,
                            apiLocale,
                            bearerToken,
                            ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Debug(ex, $"{version.LogTag} Official character achievement fetch failed for {character.SourceLabel}.");
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
                            ct,
                            version).ConfigureAwait(false);
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
            CancellationToken ct,
            WowVersion version = null)
        {
            version = version ?? WowVersion.Retail;
            if (settings?.UseDataForAzerothForWowRarity != true ||
                string.IsNullOrEmpty(version.DataForAzerothPath) ||
                achievements == null ||
                achievements.Count == 0)
            {
                return;
            }

            try
            {
                var rarity = await _client.GetDataForAzerothWowAchievementRarityAsync(version, ct).ConfigureAwait(false);
                if (rarity == null || rarity.Count == 0)
                {
                    // The client has already said why, once per session and with what to do about it.
                    // Achievements are unaffected, and previously stored rarity is preserved rather
                    // than overwritten, so there is nothing further to report here.
                    _logger?.Debug($"{version.LogTag} No Data for Azeroth rarity available this run; keeping any stored percentages.");
                    return;
                }

                var updated = ApplyDataForAzerothRarity(achievements, rarity);
                _logger?.Info($"{version.LogTag} Applied Data for Azeroth rarity to {updated}/{achievements.Count} achievements.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"{version.LogTag} Data for Azeroth rarity enrichment failed. Continuing without WoW global rarity percentages.");
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
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                // An achievement the loaded document does not list has no tracked earner, so it
                // reads as 0% rather than the default Common tier.
                var percent = rarityPercentByAchievementId.TryGetValue(apiName, out var listed) ? listed : 0d;
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
            CancellationToken ct,
            WowVersion version = null)
        {
            version = version ?? WowVersion.Retail;
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
                            ct,
                            version.NamespacePrefix).ConfigureAwait(false);
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

            var fallbackMediaHref = BuildOfficialAchievementMediaHref(definitionHref, region, id, version.NamespacePrefix);
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
                Tags = IsOfficiallyUnobtainable(definition) ? "Unobtainable" : null,
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

        internal static string BuildOfficialAchievementMediaHref(string definitionHref, string region, int achievementId, string namespacePrefix = "")
        {
            if (achievementId <= 0)
            {
                return null;
            }

            var normalizedRegion = NormalizeOfficialRegion(region);
            var authority = $"https://{normalizedRegion}.api.blizzard.com";
            var namespaceValue = $"static-{namespacePrefix ?? string.Empty}{normalizedRegion}";

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

        private static WowOfficialCharacterTarget BuildConfiguredCharacterTarget(BattleNetSettings settings, WowVersion version)
        {
            var realmSlug = NormalizeRealmSlug(version.RealmSlug(settings));
            var character = version.Character(settings)?.Trim();
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.WowRegion) ||
                string.IsNullOrWhiteSpace(realmSlug) ||
                string.IsNullOrWhiteSpace(character))
            {
                return null;
            }

            return new WowOfficialCharacterTarget
            {
                Region = settings.WowRegion,
                RealmSlug = realmSlug,
                CharacterName = character,
                SourceLabel = $"{settings.WowRegion}/{realmSlug}/{character}"
            };
        }

        /// <summary>
        /// Accepts a realm slug or a typed realm name ("Tarren Mill", "Shek'zeer") and returns the
        /// slug form the API expects ("tarren-mill", "shekzeer").
        /// </summary>
        internal static string NormalizeRealmSlug(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var slug = value.Trim().ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
            return string.Join("-", slug.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal const int MinimumProfileLevel = 10;

        /// <summary>
        /// Whether a version reads the signed-in account's characters. Forever has no named realms,
        /// so it always does.
        /// </summary>
        internal static bool UsesAccount(BattleNetSettings settings, WowVersion version) =>
            version == WowVersion.Forever ||
            settings?.WowCharacterSource != WowCharacterSource.Character;

        internal static List<WowOfficialCharacterTarget> ExtractAccountCharacters(
            string region,
            WowAccountProfileResponse accountProfile)
        {
            var characters = new List<WowOfficialCharacterTarget>();
            foreach (var account in accountProfile?.WowAccounts ?? Enumerable.Empty<WowAccountProfileAccount>())
            {
                foreach (var entry in account?.Characters ?? Enumerable.Empty<WowAccountProfileCharacterEntry>())
                {
                    // The profile API answers 404 for characters below level 10.
                    if (entry == null || (entry.Level > 0 && entry.Level < MinimumProfileLevel))
                    {
                        continue;
                    }

                    var target = new WowOfficialCharacterTarget
                    {
                        Region = region,
                        RealmSlug = entry.Realm?.Slug,
                        CharacterName = entry.Name,
                        ProfileHref = entry.Character?.Href,
                        Level = entry.Level
                    };
                    if (!target.HasRealmAndName && string.IsNullOrWhiteSpace(target.ProfileHref))
                    {
                        continue;
                    }

                    target.SourceLabel = target.HasRealmAndName
                        ? $"{region}/{target.RealmSlug}/{target.CharacterName}"
                        : $"{region}/character {entry.Id.ToString(CultureInfo.InvariantCulture)}";
                    characters.Add(target);
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
                    (!character.HasRealmAndName && string.IsNullOrWhiteSpace(character.ProfileHref)))
                {
                    continue;
                }

                var key = character.HasRealmAndName
                    ? $"{character.Region}/{character.RealmSlug}/{character.CharacterName}"
                    : character.ProfileHref.Split('?')[0];
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

        internal sealed class WowOfficialCharacterTarget
        {
            public string Region { get; set; }
            public string RealmSlug { get; set; }
            public string CharacterName { get; set; }
            public string ProfileHref { get; set; }
            public int Level { get; set; }
            public string SourceLabel { get; set; }

            public bool HasRealmAndName =>
                !string.IsNullOrWhiteSpace(RealmSlug) && !string.IsNullOrWhiteSpace(CharacterName);
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
