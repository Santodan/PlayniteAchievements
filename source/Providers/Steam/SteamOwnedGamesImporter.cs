using HtmlAgilityPack;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Providers.ImportedGameMetadata;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamOwnedGamesImporter
    {
        private sealed class ImportedProviderPageMetadata
        {
            public string Name { get; set; }

            public string Description { get; set; }

            public string IconUrl { get; set; }

            public string Url { get; set; }

            public string UrlName { get; set; }
        }

        internal sealed class ImportProgressInfo
        {
            public string Text { get; set; }

            public int? Current { get; set; }

            public int? Max { get; set; }

            public bool IsIndeterminate { get; set; }
        }

        internal sealed class ImportResult
        {
            public bool IsAuthenticated { get; set; }

            public bool HasSteamLibraryPlugin { get; set; }

            public int OwnedCount { get; set; }

            public int ExistingCount { get; set; }

            public int ImportedCount { get; set; }

            public int UpdatedCount { get; set; }

            public int FailedCount { get; set; }

            public bool FamilyShareEnabled { get; set; }

            public bool FamilyShareAttempted { get; set; }

            public int FamilySharedCount { get; set; }

            public bool WasCanceled { get; set; }

            public List<Guid> ImportedGameIds { get; } = new List<Guid>();
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SteamSessionManager _sessionManager;

        public SteamOwnedGamesImporter(
            IPlayniteAPI api,
            ILogger logger,
            SteamSessionManager sessionManager)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        }

        public async Task<ImportResult> ImportOwnedGamesAsync(
            CancellationToken ct,
            IProgress<ImportProgressInfo> progress = null,
            SteamSettings steamSettings = null)
        {
            var result = new ImportResult();
            ReportProgress(progress, "Checking Steam authentication...", isIndeterminate: true);

            var probe = await _sessionManager.ProbeAuthStateAsync(ct).ConfigureAwait(false);
            var steamUserId = probe?.UserId?.Trim();
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(steamUserId))
            {
                _logger?.Warn("[SteamAch] Owned-games import skipped because Steam web auth is not available.");
                return result;
            }

            result.IsAuthenticated = true;
            steamSettings = steamSettings ?? ProviderRegistry.Settings<SteamSettings>();
            result.FamilyShareEnabled = steamSettings?.IncludeFamilySharedGames == true;
            var metadataSourceId = (steamSettings?.ImportedGameMetadataSourceId ?? string.Empty).Trim();
            var usesBuiltInMetadata = ImportedGameMetadataSourceCatalog.IsBuiltInSource(metadataSourceId);
            var selectedMetadataPlugin = string.IsNullOrWhiteSpace(metadataSourceId) || usesBuiltInMetadata
                ? null
                : ImportedGameMetadataSourceCatalog.ResolveMetadataPlugin(_api, _logger, metadataSourceId);
            var overwriteExisting = (steamSettings?.ExistingGameImportBehavior ?? SteamExistingGameImportBehavior.OverwriteExisting)
                == SteamExistingGameImportBehavior.OverwriteExisting;

            _logger?.Info($"[SteamAch] Selected import metadata provider id='{metadataSourceId}', resolved='{selectedMetadataPlugin?.Name ?? (usesBuiltInMetadata ? metadataSourceId : "<automatic>")}'.");

            var steamLibraryPlugin = ResolveSteamLibraryPlugin();
            if (steamLibraryPlugin == null)
            {
                _logger?.Warn("[SteamAch] Owned-games import skipped because the Steam library plugin could not be resolved.");
                return result;
            }

            result.HasSteamLibraryPlugin = true;

            using (var metadataDownloader = steamLibraryPlugin.GetMetadataDownloader())
            using (var steamClient = new SteamHttpClient(_api, _logger, _sessionManager, pluginUserDataPath: null))
            {
                ReportProgress(progress, "Resolving Steam library games...", isIndeterminate: true);
                var resolveResult = await ResolveOwnedGamesAsync(steamClient, steamUserId, ct, progress, steamSettings).ConfigureAwait(false);
                var ownedGames = resolveResult.Games;
                result.WasCanceled = resolveResult.WasCanceled;
                result.OwnedCount = ownedGames.Count;
                result.FamilySharedCount = ownedGames.Count(game => string.Equals(game?.LibrarySourceName, "Steam Family Sharing", StringComparison.OrdinalIgnoreCase));
                if (ownedGames.Count == 0)
                {
                    return result;
                }

                var existingSteamGamesByAppId = _api.Database.Games
                    .Where(game => game != null && game.PluginId == SteamDataProvider.SteamPluginId)
                    .Select(game => new { Game = game, AppId = TryParseSteamAppId(game) })
                    .Where(entry => entry.AppId > 0)
                    .GroupBy(entry => entry.AppId)
                    .ToDictionary(group => group.Key, group => group.First().Game);

                var existingSteamAppIds = new HashSet<int>(existingSteamGamesByAppId.Keys);

                var importGames = ownedGames
                    .Where(game => game?.AppId > 0)
                    .GroupBy(game => game.AppId.Value)
                    .Select(group => group.First())
                    .Where(game => overwriteExisting || !existingSteamAppIds.Contains(game.AppId.Value))
                    .ToList();

                result.ExistingCount = Math.Max(0, result.OwnedCount - importGames.Count(game => !existingSteamAppIds.Contains(game.AppId.Value)));
                if (importGames.Count == 0)
                {
                    return result;
                }

                ReportProgress(progress, "Importing Steam games...", current: 0, max: importGames.Count, isIndeterminate: false);
                var importCancellationToken = result.WasCanceled ? CancellationToken.None : ct;

                _api.Database.Games.BeginBufferUpdate();
                try
                {
                    for (var index = 0; index < importGames.Count; index++)
                    {
                        importCancellationToken.ThrowIfCancellationRequested();
                        var ownedGame = importGames[index];
                        var sourceName = string.IsNullOrWhiteSpace(ownedGame?.LibrarySourceName) ? "Steam" : ownedGame.LibrarySourceName.Trim();
                        Game existingGame = null;
                        var isExistingGame = ownedGame?.AppId > 0 && existingSteamGamesByAppId.TryGetValue(ownedGame.AppId.Value, out existingGame);
                        ReportProgress(
                            progress,
                            $"{(isExistingGame ? "Updating" : "Importing")} {ownedGame?.Name ?? "Steam game"} [{sourceName}] ({index + 1}/{importGames.Count})...",
                            current: index + 1,
                            max: importGames.Count,
                            isIndeterminate: false);

                        try
                        {
                            Game imported = null;
                            if (isExistingGame)
                            {
                                ApplyOwnedGameMetadata(existingGame, ownedGame);
                                imported = existingGame;
                            }
                            else
                            {
                                imported = _api.Database.ImportGame(BuildMetadata(ownedGame), steamLibraryPlugin);
                            }

                            if (imported == null)
                            {
                                result.FailedCount++;
                                continue;
                            }

                            ApplyImportedMetadata(imported, ownedGame.AppId ?? 0, metadataSourceId, selectedMetadataPlugin, metadataDownloader);

                            if (isExistingGame)
                            {
                                result.UpdatedCount++;
                            }
                            else
                            {
                                result.ImportedCount++;
                                result.ImportedGameIds.Add(imported.Id);
                                existingSteamGamesByAppId[ownedGame.AppId.Value] = imported;
                                existingSteamAppIds.Add(ownedGame.AppId.Value);
                            }

                            ReportProgress(
                                progress,
                                $"{(isExistingGame ? "Updated" : "Imported")} {ownedGame?.Name ?? "Steam game"} [{sourceName}] ({index + 1}/{importGames.Count})",
                                current: index + 1,
                                max: importGames.Count,
                                isIndeterminate: false);
                        }
                        catch (Exception ex)
                        {
                            result.FailedCount++;
                            _logger?.Warn(ex, $"[SteamAch] Failed importing owned Steam game appId={ownedGame?.AppId} name='{ownedGame?.Name}'.");
                        }
                    }
                }
                finally
                {
                    _api.Database.Games.EndBufferUpdate();
                }
            }

            _logger?.Info($"[SteamAch] Owned-games import finished. owned={result.OwnedCount} existing={result.ExistingCount} imported={result.ImportedCount} updated={result.UpdatedCount} failed={result.FailedCount}");
            return result;
        }

        private void ApplyOwnedGameMetadata(Game targetGame, OwnedGame ownedGame)
        {
            if (targetGame == null || ownedGame == null)
            {
                return;
            }

            var metadata = BuildMetadata(ownedGame);
            if (metadata == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Name))
            {
                targetGame.Name = metadata.Name;
                targetGame.SortingName = metadata.SortingName;
            }

            if (!string.IsNullOrWhiteSpace(metadata.GameId))
            {
                targetGame.GameId = metadata.GameId;
            }

            var sourceName = (metadata.Source as MetadataNameProperty)?.Name;
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var source = _api.Database.Sources.FirstOrDefault(item => item != null && string.Equals(item.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                    ?? _api.Database.Sources.Add(sourceName);
                if (source != null)
                {
                    targetGame.SourceId = source.Id;
                }
            }

            targetGame.IsInstalled = metadata.IsInstalled;
            targetGame.Playtime = metadata.Playtime;
            targetGame.LastActivity = metadata.LastActivity;
        }

        private sealed class ResolveOwnedGamesResult
        {
            public List<OwnedGame> Games { get; } = new List<OwnedGame>();

            public bool WasCanceled { get; set; }
        }

        private async Task<ResolveOwnedGamesResult> ResolveOwnedGamesAsync(
            SteamHttpClient steamClient,
            string steamUserId,
            CancellationToken ct,
            IProgress<ImportProgressInfo> progress,
            SteamSettings steamSettings)
        {
            var apiKey = steamSettings?.SteamApiKey?.Trim();
            var resolvedGames = new ResolveOwnedGamesResult();

            if (!string.IsNullOrWhiteSpace(apiKey)
                && !string.IsNullOrWhiteSpace(steamUserId))
            {
                var steamApiClient = new SteamApiClient(steamClient.ApiHttpClient, _logger);
                if (await steamApiClient.ValidateApiKeyAsync(apiKey, steamUserId, ct).ConfigureAwait(false))
                {
                    _logger?.Info("[SteamAch] Loading owned Steam games from Steam Web API for owned-games import.");
                    resolvedGames.Games.AddRange(await steamApiClient.GetOwnedGamesDetailedAsync(apiKey, steamUserId, includePlayedFreeGames: true, ct).ConfigureAwait(false));

                    if (steamSettings?.IncludeFamilySharedGames == true)
                    {
                        _logger?.Info("[SteamAch] Family-shared Steam import is enabled.");
                        _logger?.Info("[SteamAch] Loading family-shared Steam games from Steam Family Groups Web API.");
                        var familySharedAppIds = await steamApiClient.GetFamilySharedAppIdsAsync(apiKey, steamUserId, ct).ConfigureAwait(false);
                        if (familySharedAppIds.Count > 0)
                        {
                            var familyResolution = await steamClient.ResolveOwnedGamesFromAppIdsAsync(
                                familySharedAppIds,
                                "Steam Family Sharing",
                                ct,
                                CreateResolutionProgress(progress, "Steam Family Sharing games")).ConfigureAwait(false);
                            resolvedGames.Games.AddRange(familyResolution.Games);
                            resolvedGames.WasCanceled = familyResolution.WasCanceled;
                        }
                    }

                    var deduplicatedGames = DeduplicateGamesByAppId(resolvedGames.Games);
                    resolvedGames.Games.Clear();
                    resolvedGames.Games.AddRange(deduplicatedGames);
                    return resolvedGames;
                }

                _logger?.Warn("[SteamAch] Steam Web API key did not validate for owned-games import. Falling back to authenticated store session.");
            }

            var ownedResolution = await steamClient
                .GetOwnedGamesFromSessionAsync(ct, CreateResolutionProgress(progress, "owned Steam games"))
                .ConfigureAwait(false);
            resolvedGames.Games.AddRange(ownedResolution.Games);
            resolvedGames.WasCanceled = ownedResolution.WasCanceled;

            if (!resolvedGames.WasCanceled && steamSettings?.IncludeFamilySharedGames == true)
            {
                var familyResolution = await steamClient
                    .GetFamilySharedGamesFromSessionAsync(steamUserId, ct, CreateResolutionProgress(progress, "Steam Family Sharing games"))
                    .ConfigureAwait(false);
                resolvedGames.Games.AddRange(familyResolution.Games);
                resolvedGames.WasCanceled = familyResolution.WasCanceled;
            }

            var deduplicatedResolvedGames = DeduplicateGamesByAppId(resolvedGames.Games);
            resolvedGames.Games.Clear();
            resolvedGames.Games.AddRange(deduplicatedResolvedGames);
            if (resolvedGames.WasCanceled)
            {
                _logger?.Info($"[SteamAch] Steam import was canceled after resolving {resolvedGames.Games.Count} game(s). Importing the already resolved games.");
            }

            return resolvedGames;
        }

        private IProgress<SteamHttpClient.OwnedGamesResolutionProgressInfo> CreateResolutionProgress(
            IProgress<ImportProgressInfo> progress,
            string label)
        {
            if (progress == null)
            {
                return null;
            }

            return new Progress<SteamHttpClient.OwnedGamesResolutionProgressInfo>(info =>
            {
                if (info == null)
                {
                    return;
                }

                ReportProgress(
                    progress,
                    $"Loading {label} ({info.Current}/{info.Max})...",
                    current: info.Current,
                    max: info.Max,
                    isIndeterminate: false);
            });
        }


        private void ApplyImportedMetadata(
            Game importedGame,
            int appId,
            string metadataSourceId,
            MetadataPlugin selectedMetadataPlugin,
            LibraryMetadataProvider metadataDownloader)
        {
            if (importedGame == null)
            {
                return;
            }

            try
            {
                if (ImportedGameMetadataSourceCatalog.IsBuiltInSource(metadataSourceId))
                {
                    ApplyBuiltInMetadata(importedGame, appId, metadataSourceId);
                }
                else if (selectedMetadataPlugin != null)
                {
                    ApplyMetadataPlugin(importedGame, appId, selectedMetadataPlugin);
                }
                else
                {
                    ApplyDownloadedMetadata(importedGame, metadataDownloader);
                }

                NormalizeImportedGameMetadata(importedGame, appId);
                _api.Database.Games.Update(importedGame);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed applying import metadata for appId={appId} game '{importedGame?.Name}'.");
            }
        }
        private static void ReportProgress(
            IProgress<ImportProgressInfo> progress,
            string text,
            int? current = null,
            int? max = null,
            bool isIndeterminate = true)
        {
            progress?.Report(new ImportProgressInfo
            {
                Text = text,
                Current = current,
                Max = max,
                IsIndeterminate = isIndeterminate
            });
        }

        private static List<OwnedGame> DeduplicateGamesByAppId(IEnumerable<OwnedGame> games)
        {
            return games?
                .Where(game => game?.AppId > 0)
                .GroupBy(game => game.AppId.Value)
                .Select(group => group.First())
                .ToList() ?? new List<OwnedGame>();
        }

        private static int TryParseSteamAppId(Game game)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.GameId))
            {
                return 0;
            }

            return int.TryParse(game.GameId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)
                ? appId
                : 0;
        }

        private void ApplyDownloadedMetadata(Game importedGame, LibraryMetadataProvider metadataDownloader)
        {
            if (importedGame == null || metadataDownloader == null)
            {
                return;
            }

            try
            {
                var metadata = metadataDownloader.GetMetadata(importedGame);
                if (metadata == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(metadata.Name))
                {
                    importedGame.Name = metadata.Name;
                }

                if (!string.IsNullOrWhiteSpace(metadata.SortingName))
                {
                    importedGame.SortingName = metadata.SortingName;
                }

                if (!string.IsNullOrWhiteSpace(metadata.Description))
                {
                    importedGame.Description = metadata.Description;
                }

                var iconId = PersistMetadataFile(importedGame.Id, metadata.Icon);
                if (!string.IsNullOrWhiteSpace(iconId))
                {
                    importedGame.Icon = iconId;
                }

                var coverId = PersistMetadataFile(importedGame.Id, metadata.CoverImage);
                if (!string.IsNullOrWhiteSpace(coverId))
                {
                    importedGame.CoverImage = coverId;
                }

                var backgroundId = PersistMetadataFile(importedGame.Id, metadata.BackgroundImage);
                if (!string.IsNullOrWhiteSpace(backgroundId))
                {
                    importedGame.BackgroundImage = backgroundId;
                }

                if (metadata.ReleaseDate != null)
                {
                    importedGame.ReleaseDate = metadata.ReleaseDate;
                }

                if (metadata.CriticScore.HasValue)
                {
                    importedGame.CriticScore = metadata.CriticScore;
                }

                if (metadata.CommunityScore.HasValue)
                {
                    importedGame.CommunityScore = metadata.CommunityScore;
                }

                if (metadata.Platforms?.Count > 0)
                {
                    ReplaceCollection(importedGame.Platforms, _api.Database.Platforms.Add(metadata.Platforms));
                }

                if (metadata.Genres?.Count > 0)
                {
                    ReplaceCollection(importedGame.Genres, _api.Database.Genres.Add(metadata.Genres));
                }

                if (metadata.Developers?.Count > 0)
                {
                    ReplaceCollection(importedGame.Developers, _api.Database.Companies.Add(metadata.Developers));
                }

                if (metadata.Publishers?.Count > 0)
                {
                    ReplaceCollection(importedGame.Publishers, _api.Database.Companies.Add(metadata.Publishers));
                }

                if (metadata.Tags?.Count > 0)
                {
                    ReplaceCollection(importedGame.Tags, _api.Database.Tags.Add(metadata.Tags));
                }

                if (metadata.Categories?.Count > 0)
                {
                    ReplaceCollection(importedGame.Categories, _api.Database.Categories.Add(metadata.Categories));
                }

                if (metadata.Features?.Count > 0)
                {
                    ReplaceCollection(importedGame.Features, _api.Database.Features.Add(metadata.Features));
                }

                if (metadata.AgeRatings?.Count > 0)
                {
                    ReplaceCollection(importedGame.AgeRatings, _api.Database.AgeRatings.Add(metadata.AgeRatings));
                }

                if (metadata.Regions?.Count > 0)
                {
                    ReplaceCollection(importedGame.Regions, _api.Database.Regions.Add(metadata.Regions));
                }

                if (metadata.Series?.Count > 0)
                {
                    ReplaceCollection(importedGame.Series, _api.Database.Series.Add(metadata.Series));
                }

                if (metadata.Links?.Count > 0)
                {
                    ReplaceCollection(importedGame.Links, metadata.Links);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed applying downloaded Steam metadata for imported game '{importedGame?.Name}'.");
            }
        }

        private void ApplyBuiltInMetadata(Game importedGame, int appId, string metadataSourceId)
        {
            if (importedGame == null || appId <= 0)
            {
                return;
            }

            var applied = false;
            if (string.Equals(metadataSourceId, ImportedGameMetadataSourceCatalog.SteamHuntersId, StringComparison.OrdinalIgnoreCase))
            {
                applied = ApplySteamHuntersMetadata(importedGame, appId);
            }
            else if (string.Equals(metadataSourceId, ImportedGameMetadataSourceCatalog.CompletionistId, StringComparison.OrdinalIgnoreCase))
            {
                applied = ApplyCompletionistMetadata(importedGame, appId);
            }

            if (applied || ShouldApplySteamStoreFallback(importedGame, appId))
            {
                ApplySteamStoreFallbackMetadata(importedGame, appId);
            }
        }

        private bool ApplyMetadataPlugin(Game importedGame, int appId, MetadataPlugin metadataPlugin)
        {
            if (importedGame == null || appId <= 0 || metadataPlugin == null)
            {
                return false;
            }

            try
            {
                ClearImportedMetadataForOverwrite(importedGame);
                var lookupGame = CreateMetadataPluginLookupGame(importedGame, appId, metadataPlugin);

                using (var provider = metadataPlugin.GetMetadataProvider(new MetadataRequestOptions(lookupGame, true)))
                {
                    if (provider == null)
                    {
                        return false;
                    }

                    var args = new GetMetadataFieldArgs();
                    var applied = false;

                    var name = provider.GetName(args);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        importedGame.Name = name;
                        importedGame.SortingName = name;
                        applied = true;
                    }

                    var description = provider.GetDescription(args);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        importedGame.Description = PrepareProviderMetadataDescription(description);
                        applied = true;
                    }

                    var iconId = PersistMetadataFile(importedGame.Id, provider.GetIcon(args));
                    if (!string.IsNullOrWhiteSpace(iconId))
                    {
                        importedGame.Icon = iconId;
                        applied = true;
                    }

                    var coverId = PersistMetadataFile(importedGame.Id, provider.GetCoverImage(args));
                    if (!string.IsNullOrWhiteSpace(coverId))
                    {
                        importedGame.CoverImage = coverId;
                        applied = true;
                    }

                    var backgroundId = PersistMetadataFile(importedGame.Id, provider.GetBackgroundImage(args));
                    if (!string.IsNullOrWhiteSpace(backgroundId))
                    {
                        importedGame.BackgroundImage = backgroundId;
                        applied = true;
                    }

                    var releaseDate = provider.GetReleaseDate(args);
                    if (releaseDate != null)
                    {
                        importedGame.ReleaseDate = releaseDate;
                        applied = true;
                    }

                    var criticScore = provider.GetCriticScore(args);
                    if (criticScore.HasValue)
                    {
                        importedGame.CriticScore = criticScore;
                        applied = true;
                    }

                    var communityScore = provider.GetCommunityScore(args);
                    if (communityScore.HasValue)
                    {
                        importedGame.CommunityScore = communityScore;
                        applied = true;
                    }

                    var platforms = provider.GetPlatforms(args)?.ToList();
                    var genres = provider.GetGenres(args)?.ToList();
                    var developers = provider.GetDevelopers(args)?.ToList();
                    var publishers = provider.GetPublishers(args)?.ToList();
                    var tags = provider.GetTags(args)?.ToList();
                    var features = provider.GetFeatures(args)?.ToList();
                    var ageRatings = provider.GetAgeRatings(args)?.ToList();
                    var regions = provider.GetRegions(args)?.ToList();
                    var series = provider.GetSeries(args)?.ToList();

                    if (platforms?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Platforms, _api.Database.Platforms.Add(platforms));
                        applied = true;
                    }

                    if (genres?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Genres, _api.Database.Genres.Add(genres));
                        applied = true;
                    }

                    if (developers?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Developers, _api.Database.Companies.Add(developers));
                        applied = true;
                    }

                    if (publishers?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Publishers, _api.Database.Companies.Add(publishers));
                        applied = true;
                    }

                    if (tags?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Tags, _api.Database.Tags.Add(tags));
                        applied = true;
                    }

                    if (features?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Features, _api.Database.Features.Add(features));
                        applied = true;
                    }

                    if (ageRatings?.Count > 0)
                    {
                        ReplaceCollection(importedGame.AgeRatings, _api.Database.AgeRatings.Add(ageRatings));
                        applied = true;
                    }

                    if (regions?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Regions, _api.Database.Regions.Add(regions));
                        applied = true;
                    }

                    if (series?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Series, _api.Database.Series.Add(series));
                        applied = true;
                    }

                    var links = provider.GetLinks(args)?.ToList();
                    if (links?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Links, links);
                        applied = true;
                    }

                    return applied;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed applying metadata plugin '{metadataPlugin?.Name}' for appId={appId}.");
                return false;
            }
        }

        private bool ApplySteamHuntersMetadata(Game importedGame, int appId)
        {
            var url = $"https://steamhunters.com/apps/{appId}/achievements";
            return ApplyImportedProviderPageMetadata(
                importedGame,
                TryFetchImportedMetadataPage(
                    url,
                    document => new ImportedProviderPageMetadata
                    {
                        Name = FirstNonEmpty(ExtractMetaContent(document, "og:title"), TryExtractPageTitle(document), ExtractHeadingText(document)),
                        Description = FirstNonEmpty(FindTextAfterHeading(document, "Description"), ExtractMetaContent(document, "description")),
                        IconUrl = ExtractImageUrl(document, url, "//img[contains(concat(' ', normalize-space(@class), ' '), ' image-rounded ') and contains(concat(' ', normalize-space(@class), ' '), ' image-1em ')]"),
                        Url = url,
                        UrlName = "SteamHunters"
                    }));
        }

        private bool ApplyCompletionistMetadata(Game importedGame, int appId)
        {
            var url = $"https://completionist.me/steam/app/{appId}/achievements";
            return ApplyImportedProviderPageMetadata(
                importedGame,
                TryFetchImportedMetadataPage(
                    url,
                    document => new ImportedProviderPageMetadata
                    {
                        Name = FirstNonEmpty(ExtractMetaContent(document, "og:title"), TryExtractPageTitle(document), ExtractHeadingText(document)),
                        Description = FirstNonEmpty(ExtractMetaContent(document, "description"), FindTextAfterHeading(document, "Description")),
                        IconUrl = ExtractImageUrl(document, url, "//*[contains(concat(' ', normalize-space(@class), ' '), ' dropdown-toggle ')]//img[@src]"),
                        Url = url,
                        UrlName = "Completionist.me"
                    }));
        }

        private ImportedProviderPageMetadata TryFetchImportedMetadataPage(string url, Func<HtmlDocument, ImportedProviderPageMetadata> extractor)
        {
            if (string.IsNullOrWhiteSpace(url) || extractor == null)
            {
                return null;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36";
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                request.Referer = BuildRequestReferer(url);
                request.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    var html = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        return null;
                    }

                    var document = new HtmlDocument();
                    document.LoadHtml(html);
                    return extractor(document);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed downloading imported metadata page '{url}'.");
                return null;
            }
        }

        private bool ApplyImportedProviderPageMetadata(Game importedGame, ImportedProviderPageMetadata metadata)
        {
            if (importedGame == null || metadata == null)
            {
                return false;
            }

            var applied = false;

            if (!string.IsNullOrWhiteSpace(metadata.Name))
            {
                importedGame.Name = metadata.Name.Trim();
                importedGame.SortingName = importedGame.Name;
                applied = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Description))
            {
                importedGame.Description = PrepareProviderMetadataDescription(metadata.Description);
                applied = true;
            }

            var iconId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(metadata.IconUrl) ? null : new MetadataFile(metadata.IconUrl));
            if (!string.IsNullOrWhiteSpace(iconId))
            {
                importedGame.Icon = iconId;
                applied = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Url))
            {
                var links = importedGame.Links?.ToList() ?? new List<Link>();
                AddLinkIfMissing(links, metadata.UrlName ?? "Source", metadata.Url);
                ReplaceCollection(importedGame.Links, links);
                applied = true;
            }

            return applied;
        }

        private void ApplySteamStoreFallbackMetadata(Game importedGame, int appId)
        {
            if (importedGame == null || appId <= 0)
            {
                return;
            }

            try
            {
                var requestUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                using (var webClient = new WebClient())
                {
                    webClient.Headers[HttpRequestHeader.UserAgent] =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                    webClient.Headers[HttpRequestHeader.Accept] = "application/json";
                    webClient.Encoding = Encoding.UTF8;

                    var json = webClient.DownloadString(requestUrl);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return;
                    }

                    var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var envelope = root[appId.ToString(CultureInfo.InvariantCulture)] as Newtonsoft.Json.Linq.JObject;
                    if ((bool?)envelope?["success"] != true)
                    {
                        return;
                    }

                    var data = envelope["data"] as Newtonsoft.Json.Linq.JObject;
                    if (data == null)
                    {
                        return;
                    }

                    var resolvedName = ((string)data["name"])?.Trim();
                    if (!string.IsNullOrWhiteSpace(resolvedName))
                    {
                        importedGame.Name = resolvedName;
                        importedGame.SortingName = resolvedName;
                    }

                    if (string.IsNullOrWhiteSpace(importedGame.Description))
                    {
                        var description = ((string)data["about_the_game"])?.Trim();
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = ((string)data["detailed_description"])?.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = ((string)data["short_description"])?.Trim();
                        }

                        importedGame.Description = NormalizeMetadataDescription(description);
                    }

                    var iconUrl = ((string)data["capsule_image"])?.Trim();
                    if (string.IsNullOrWhiteSpace(iconUrl))
                    {
                        iconUrl = ((string)data["header_image"])?.Trim();
                    }

                    var coverUrl = ((string)data["header_image"])?.Trim();
                    var backgroundUrl = ((string)data["background_raw"])?.Trim();
                    if (string.IsNullOrWhiteSpace(backgroundUrl))
                    {
                        backgroundUrl = ((string)data["background"])?.Trim();
                    }

                    var iconId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(iconUrl) || !string.IsNullOrWhiteSpace(importedGame.Icon) ? null : new MetadataFile(iconUrl));
                    if (!string.IsNullOrWhiteSpace(iconId))
                    {
                        importedGame.Icon = iconId;
                    }

                    var coverId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(coverUrl) || !string.IsNullOrWhiteSpace(importedGame.CoverImage) ? null : new MetadataFile(coverUrl));
                    if (!string.IsNullOrWhiteSpace(coverId))
                    {
                        importedGame.CoverImage = coverId;
                    }

                    var backgroundId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(backgroundUrl) || !string.IsNullOrWhiteSpace(importedGame.BackgroundImage) ? null : new MetadataFile(backgroundUrl));
                    if (!string.IsNullOrWhiteSpace(backgroundId))
                    {
                        importedGame.BackgroundImage = backgroundId;
                    }

                    var websiteUrl = ((string)data["website"])?.Trim();
                    var links = importedGame.Links?.ToList() ?? new List<Link>();
                    AddLinkIfMissing(links, "Steam Store", $"https://store.steampowered.com/app/{appId}/");
                    AddLinkIfMissing(links, "Community Hub", $"https://steamcommunity.com/app/{appId}");
                    AddLinkIfMissing(links, "Discussions", $"https://steamcommunity.com/app/{appId}/discussions/");
                    AddLinkIfMissing(links, "Guides", $"https://steamcommunity.com/app/{appId}/guides/");
                    AddLinkIfMissing(links, "News", $"https://store.steampowered.com/news/app/{appId}");

                    var categoryIds = data["categories"]?
                        .Values<Newtonsoft.Json.Linq.JObject>()
                        .Select(category => (int?)category?["id"] ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet() ?? new HashSet<int>();
                    if (categoryIds.Contains(22))
                    {
                        AddLinkIfMissing(links, "Achievements", $"https://steamcommunity.com/stats/{appId}/achievements/");
                    }

                    if (categoryIds.Contains(30))
                    {
                        AddLinkIfMissing(links, "Workshop", $"https://steamcommunity.com/app/{appId}/workshop/");
                    }

                    if (!string.IsNullOrWhiteSpace(websiteUrl))
                    {
                        AddLinkIfMissing(links, "Website", websiteUrl);
                    }

                    if (links.Count > 0)
                    {
                        ReplaceCollection(importedGame.Links, links);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed applying Steam Store fallback metadata for appId={appId}.");
            }
        }

        private Game CreateMetadataPluginLookupGame(Game importedGame, int appId, MetadataPlugin metadataPlugin)
        {
            if (importedGame == null)
            {
                return null;
            }

            var isUniversalSteamMetadata = ImportedGameMetadataSourceCatalog.IsUniversalSteamMetadataPlugin(metadataPlugin, _api, _logger);
            var lookupName = isUniversalSteamMetadata
                ? BuildUniversalSteamMetadataLookupName(importedGame.Name, appId)
                : importedGame.Name;

            return new Game
            {
                GameId = appId > 0 ? appId.ToString(CultureInfo.InvariantCulture) : importedGame.GameId,
                PluginId = isUniversalSteamMetadata ? SteamDataProvider.SteamPluginId : importedGame.PluginId,
                Name = lookupName,
                SortingName = lookupName,
                SourceId = importedGame.SourceId
            };
        }

        private static bool IsPlaceholderSteamName(string value, int appId)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return string.Equals(value.Trim(), $"Steam App {appId}", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildUniversalSteamMetadataLookupName(string currentName, int appId)
        {
            var trimmedName = (currentName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedName) || IsPlaceholderSteamName(trimmedName, appId))
            {
                return appId.ToString(CultureInfo.InvariantCulture);
            }

            if (trimmedName.IndexOf(appId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return trimmedName;
            }

            return $"{trimmedName} {appId}";
        }

        private static bool ShouldApplySteamStoreFallback(Game importedGame, int appId)
        {
            if (importedGame == null)
            {
                return false;
            }

            if (IsPlaceholderSteamName(importedGame.Name, appId) || IsPlaceholderSteamName(importedGame.SortingName, appId))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(importedGame.Description))
            {
                return true;
            }

            return importedGame.Links == null || importedGame.Links.Count == 0;
        }

        private static void ClearImportedMetadataForOverwrite(Game importedGame)
        {
            if (importedGame == null)
            {
                return;
            }

            importedGame.Description = null;
            importedGame.Icon = null;
            importedGame.CoverImage = null;
            importedGame.BackgroundImage = null;
            importedGame.ReleaseDate = null;
            importedGame.CriticScore = null;
            importedGame.CommunityScore = null;

            importedGame.Platforms?.Clear();
            importedGame.Genres?.Clear();
            importedGame.Developers?.Clear();
            importedGame.Publishers?.Clear();
            importedGame.Tags?.Clear();
            importedGame.Categories?.Clear();
            importedGame.Features?.Clear();
            importedGame.AgeRatings?.Clear();
            importedGame.Regions?.Clear();
            importedGame.Series?.Clear();
            importedGame.Links?.Clear();
        }

        private static void AddLinkIfMissing(ICollection<Link> links, string name, string url)
        {
            if (links == null || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (links.Any(link => link != null && string.Equals(link.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            links.Add(new Link(name, url));
        }

        private static string ExtractImageUrl(HtmlDocument document, string pageUrl, string xpath)
        {
            var source = document?.DocumentNode.SelectSingleNode(xpath)?.GetAttributeValue("src", null)?.Trim();
            return ResolveAbsoluteUrl(pageUrl, source);
        }

        private static string ResolveAbsoluteUrl(string pageUrl, string candidateUrl)
        {
            if (string.IsNullOrWhiteSpace(candidateUrl))
            {
                return null;
            }

            if (Uri.TryCreate(candidateUrl.Trim(), UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.AbsoluteUri;
            }

            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
                && Uri.TryCreate(pageUri, candidateUrl.Trim(), out var resolvedUri))
            {
                return resolvedUri.AbsoluteUri;
            }

            return candidateUrl.Trim();
        }

        private static string BuildRequestReferer(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            return $"{uri.Scheme}://{uri.Host}/";
        }

        private static string ExtractMetaContent(HtmlDocument document, string propertyOrName)
        {
            if (document?.DocumentNode == null || string.IsNullOrWhiteSpace(propertyOrName))
            {
                return null;
            }

            var xpath = $"//meta[translate(@property, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{propertyOrName.ToLowerInvariant()}' or translate(@name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{propertyOrName.ToLowerInvariant()}']";
            return document.DocumentNode.SelectSingleNode(xpath)?.GetAttributeValue("content", null)?.Trim();
        }

        private static string ExtractHeadingText(HtmlDocument document)
        {
            if (document?.DocumentNode == null)
            {
                return null;
            }

            return NormalizeMetadataDescription(document.DocumentNode.SelectSingleNode("//h1")?.InnerText);
        }

        private static string TryExtractPageTitle(HtmlDocument document)
        {
            var rawTitle = document?.DocumentNode.SelectSingleNode("//title")?.InnerText;
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return null;
            }

            var normalizedTitle = NormalizeMetadataDescription(rawTitle);
            var separators = new[] { " - ", " | ", " / " };
            foreach (var separator in separators)
            {
                var separatorIndex = normalizedTitle.IndexOf(separator, StringComparison.Ordinal);
                if (separatorIndex > 0)
                {
                    normalizedTitle = normalizedTitle.Substring(0, separatorIndex).Trim();
                    break;
                }
            }

            return normalizedTitle;
        }

        private static string FindTextAfterHeading(HtmlDocument document, string headingText)
        {
            if (document?.DocumentNode == null || string.IsNullOrWhiteSpace(headingText))
            {
                return null;
            }

            var headingNodes = document.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//strong");
            if (headingNodes == null)
            {
                return null;
            }

            foreach (var heading in headingNodes)
            {
                var text = NormalizeMetadataDescription(heading.InnerText);
                if (!string.Equals(text, headingText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (var sibling = heading.NextSibling; sibling != null; sibling = sibling.NextSibling)
                {
                    if (sibling.NodeType != HtmlNodeType.Element)
                    {
                        continue;
                    }

                    var value = NormalizeMetadataDescription(sibling.InnerText);
                    if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, headingText, StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static string NormalizeMetadataDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = Regex.Replace(value, @"(?i)<br\s*/?>", "\n");
            normalized = Regex.Replace(normalized, @"(?i)</p\s*>", "\n\n");
            normalized = Regex.Replace(normalized, @"(?i)</div\s*>", "\n");
            normalized = Regex.Replace(normalized, @"(?i)</li\s*>", "\n");
            normalized = Regex.Replace(normalized, @"<[^>]+>", string.Empty);
            normalized = WebUtility.HtmlDecode(normalized ?? string.Empty);
            normalized = normalized.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
            return normalized.Trim();
        }

        private static void NormalizeImportedGameMetadata(Game importedGame, int appId)
        {
            if (importedGame == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(importedGame.SortingName) || IsPlaceholderSteamName(importedGame.SortingName, appId))
            {
                importedGame.SortingName = importedGame.Name;
            }
        }

        private static string PrepareProviderMetadataDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

        private static void ReplaceCollection<T>(ICollection<T> target, IEnumerable<T> source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        private string PersistMetadataFile(Guid gameId, MetadataFile metadataFile)
        {
            if (gameId == Guid.Empty || metadataFile == null || !metadataFile.HasImageData)
            {
                return null;
            }

            string tempFilePath = null;
            try
            {
                if (metadataFile.HasContent && metadataFile.Content?.Length > 0)
                {
                    var fileName = string.IsNullOrWhiteSpace(metadataFile.FileName)
                        ? $"steam_meta_{Guid.NewGuid():N}{GetExtensionFromPath(metadataFile.Path)}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));
                    File.WriteAllBytes(tempFilePath, metadataFile.Content);
                    return _api.Database.AddFile(tempFilePath, gameId);
                }

                if (!string.IsNullOrWhiteSpace(metadataFile.Path) && File.Exists(metadataFile.Path))
                {
                    return _api.Database.AddFile(metadataFile.Path, gameId);
                }

                if (!string.IsNullOrWhiteSpace(metadataFile.Path) && Uri.TryCreate(metadataFile.Path, UriKind.Absolute, out var uri))
                {
                    var targetExtension = GetExtensionFromPath(uri.AbsolutePath);
                    var fileName = string.IsNullOrWhiteSpace(metadataFile.FileName)
                        ? $"steam_meta_{Guid.NewGuid():N}{targetExtension}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));

                    using (var webClient = new System.Net.WebClient())
                    {
                        webClient.DownloadFile(uri, tempFilePath);
                    }

                    return _api.Database.AddFile(tempFilePath, gameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed persisting metadata file for gameId={gameId} from '{metadataFile?.Path}'.");
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFilePath) && File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string CreateMetadataTempDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "SteamMetadataImport");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetExtensionFromPath(string path)
        {
            try
            {
                var extension = Path.GetExtension(path ?? string.Empty);
                return string.IsNullOrWhiteSpace(extension) ? ".img" : extension;
            }
            catch
            {
                return ".img";
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return $"steam_meta_{Guid.NewGuid():N}.img";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? $"steam_meta_{Guid.NewGuid():N}.img" : sanitized;
        }

        private static GameMetadata BuildMetadata(Models.OwnedGame ownedGame)
        {
            var appId = ownedGame?.AppId ?? 0;
            var name = string.IsNullOrWhiteSpace(ownedGame?.Name)
                ? $"Steam App {appId}"
                : ownedGame.Name.Trim();
            var sourceName = string.IsNullOrWhiteSpace(ownedGame?.LibrarySourceName)
                ? "Steam"
                : ownedGame.LibrarySourceName.Trim();

            return new GameMetadata
            {
                Name = name,
                SortingName = name,
                GameId = appId.ToString(CultureInfo.InvariantCulture),
                Source = new MetadataNameProperty(sourceName),
                IsInstalled = false,
                Playtime = (ulong)Math.Max(0, ownedGame?.PlaytimeForever ?? 0),
                LastActivity = ownedGame?.RTimeLastPlayed.HasValue == true && ownedGame.RTimeLastPlayed.Value > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ownedGame.RTimeLastPlayed.Value).UtcDateTime
                    : (DateTime?)null
            };
        }

        private LibraryPlugin ResolveSteamLibraryPlugin()
        {
            try
            {
                return _api.Addons?.Plugins?
                    .OfType<LibraryPlugin>()
                    .FirstOrDefault(plugin => plugin != null && plugin.Id == SteamDataProvider.SteamPluginId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SteamAch] Failed resolving Steam library plugin instance.");
                return null;
            }
        }
    }
}