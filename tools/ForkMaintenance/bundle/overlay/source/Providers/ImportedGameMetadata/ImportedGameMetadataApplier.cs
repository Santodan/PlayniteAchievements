using HtmlAgilityPack;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Providers.Steam;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.ImportedGameMetadata
{
    internal sealed class ImportedGameMetadataApplier
    {
        private sealed class ImportedProviderPageMetadata
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string IconUrl { get; set; }
            public string Url { get; set; }
            public string UrlName { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly string _logPrefix;
        private readonly Dictionary<int, string> _steamGridDbIconUrlCache = new Dictionary<int, string>();
        private readonly HashSet<string> _blockedMetadataHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _steamCommunityIconRequestLock = new object();
        private readonly HashSet<int> _steamCommunityRateLimitedMissAppIds = new HashSet<int>();
        private DateTime _steamCommunityIconLastRequestUtc = DateTime.MinValue;
        private DateTime _steamCommunityIconBackoffUntilUtc = DateTime.MinValue;
        private int _steamCommunityConsecutive429Count;
        private int _steamCommunityMaxAttempts = 3;
        private bool _steamCommunityWaitDuringBackoff = true;

        public ImportedGameMetadataApplier(IPlayniteAPI api, ILogger logger, string logPrefix)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _logPrefix = string.IsNullOrWhiteSpace(logPrefix) ? "ImportMetadata" : logPrefix.Trim();
        }

        public bool ApplyToGame(
            Game importedGame,
            int appId,
            string metadataSourceId,
            MetadataPlugin selectedMetadataPlugin,
            LibraryMetadataProvider fastMetadataDownloader)
        {
            if (importedGame == null || appId <= 0)
            {
                return false;
            }

            var normalizedSourceId = ImportedGameMetadataSourceCatalog.NormalizeMetadataSourceId(_api, _logger, metadataSourceId);

            if (string.IsNullOrWhiteSpace(normalizedSourceId))
            {
                return ApplyAutomaticMetadataChain(importedGame, appId, selectedMetadataPlugin);
            }

            if (ImportedGameMetadataSourceCatalog.IsFastMethodSource(normalizedSourceId))
            {
                return ApplyFastMetadata(importedGame, appId, fastMetadataDownloader);
            }

            if (ImportedGameMetadataSourceCatalog.IsUniversalSteamMetadataSource(normalizedSourceId) && selectedMetadataPlugin != null)
            {
                return ApplyMetadataPlugin(importedGame, appId, selectedMetadataPlugin);
            }

            if (ImportedGameMetadataSourceCatalog.IsBuiltInSource(normalizedSourceId))
            {
                return ApplyBuiltInMetadata(importedGame, appId, normalizedSourceId);
            }

            if (selectedMetadataPlugin != null)
            {
                return ApplyMetadataPlugin(importedGame, appId, selectedMetadataPlugin);
            }

            if (fastMetadataDownloader != null)
            {
                return ApplyFastMetadata(importedGame, appId, fastMetadataDownloader);
            }

            _logger?.Info($"[{_logPrefix}] No metadata source was available for appId={appId}; skipping metadata download.");
            return false;
        }

        public IReadOnlyCollection<int> ConsumeRateLimitedIconMissAppIds()
        {
            lock (_steamCommunityIconRequestLock)
            {
                if (_steamCommunityRateLimitedMissAppIds.Count == 0)
                {
                    return Array.Empty<int>();
                }

                var appIds = _steamCommunityRateLimitedMissAppIds.OrderBy(id => id).ToList();
                _steamCommunityRateLimitedMissAppIds.Clear();
                return appIds;
            }
        }

        public void ConfigureSteamCommunityIconLookupPolicy(int maxAttempts, bool waitDuringBackoff)
        {
            lock (_steamCommunityIconRequestLock)
            {
                _steamCommunityMaxAttempts = Math.Max(1, Math.Min(8, maxAttempts));
                _steamCommunityWaitDuringBackoff = waitDuringBackoff;
            }
        }

        public bool TryApplyIconFallbackOnly(Game importedGame, int appId)
        {
            if (importedGame == null || appId <= 0 || importedGame.Id == Guid.Empty)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(importedGame.Icon))
            {
                return true;
            }

            var iconUrl = TryGetSteamGridDbIconUrl(appId);
            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                return false;
            }

            var iconId = PersistMetadataFile(importedGame.Id, new MetadataFile(iconUrl));
            if (string.IsNullOrWhiteSpace(iconId))
            {
                return false;
            }

            importedGame.Icon = iconId;
            return true;
        }

        private bool ApplyAutomaticMetadataChain(
            Game importedGame,
            int appId,
            MetadataPlugin selectedMetadataPlugin)
        {
            // Automatic chain:
            // 1) Universal Steam Metadata
            // 2) SteamHunters + SteamGridDB icon fallback
            // 3) Completionist.me + SteamGridDB icon fallback
            // 4) IGDB

            var universalPlugin = selectedMetadataPlugin;
            if (universalPlugin == null || !ImportedGameMetadataSourceCatalog.IsUniversalSteamMetadataPlugin(universalPlugin, _api, _logger))
            {
                universalPlugin = ImportedGameMetadataSourceCatalog.ResolveUniversalSteamMetadataPlugin(_api, _logger);
            }

            _logger?.Debug($"[{_logPrefix}] Automatic metadata chain start for appId={appId}. universalPlugin={(universalPlugin?.Name ?? "<none>")}");

            if (universalPlugin != null && ApplyMetadataPlugin(importedGame, appId, universalPlugin))
            {
                _logger?.Debug($"[{_logPrefix}] Automatic metadata chain succeeded at stage=UniversalSteamMetadata for appId={appId}.");
                return true;
            }

            _logger?.Debug($"[{_logPrefix}] Automatic metadata chain stage=UniversalSteamMetadata failed for appId={appId}; trying SteamHunters+SGDB.");

            if (ApplyBuiltInAutomaticStage(importedGame, appId, ImportedGameMetadataSourceCatalog.SteamHuntersId))
            {
                _logger?.Debug($"[{_logPrefix}] Automatic metadata chain succeeded at stage=SteamHunters+SGDB for appId={appId}.");
                return true;
            }

            _logger?.Debug($"[{_logPrefix}] Automatic metadata chain stage=SteamHunters+SGDB failed for appId={appId}; trying Completionist+SGDB.");

            if (ApplyBuiltInAutomaticStage(importedGame, appId, ImportedGameMetadataSourceCatalog.CompletionistId))
            {
                _logger?.Debug($"[{_logPrefix}] Automatic metadata chain succeeded at stage=Completionist+SGDB for appId={appId}.");
                return true;
            }

            var igdbPlugin = ImportedGameMetadataSourceCatalog.ResolveIgdbMetadataPlugin(_api, _logger);
            if (igdbPlugin != null && ApplyMetadataPlugin(importedGame, appId, igdbPlugin))
            {
                _logger?.Debug($"[{_logPrefix}] Automatic metadata chain succeeded at stage=IGDB for appId={appId}.");
                return true;
            }

            _logger?.Debug($"[{_logPrefix}] Automatic metadata chain exhausted all stages for appId={appId}.");

            return false;
        }

        private bool ApplyBuiltInAutomaticStage(Game importedGame, int appId, string metadataSourceId)
        {
            var isSteamHuntersSource = string.Equals(metadataSourceId, ImportedGameMetadataSourceCatalog.SteamHuntersId, StringComparison.OrdinalIgnoreCase);
            var isCompletionistSource = string.Equals(metadataSourceId, ImportedGameMetadataSourceCatalog.CompletionistId, StringComparison.OrdinalIgnoreCase);

            if (!isSteamHuntersSource && !isCompletionistSource)
            {
                return false;
            }

            var applied = isSteamHuntersSource
                ? ApplySteamHuntersMetadata(importedGame, appId)
                : ApplyCompletionistMetadata(importedGame, appId);

            // Prefer SGDB icon when available for built-in automatic stages.
            importedGame.Icon = null;
            var storeApplied = false;
            if (applied || ShouldApplySteamStoreFallback(importedGame, appId))
            {
                storeApplied = ApplySteamStoreFallbackMetadata(importedGame, appId, includeMedia: true, includeIconFallback: true);
            }

            return applied || storeApplied;
        }

        private bool ApplyBuiltInMetadata(Game importedGame, int appId, string metadataSourceId)
        {
            var isSteamHuntersSource = string.Equals(metadataSourceId, ImportedGameMetadataSourceCatalog.SteamHuntersId, StringComparison.OrdinalIgnoreCase);
            var isCompletionistSource = string.Equals(metadataSourceId, ImportedGameMetadataSourceCatalog.CompletionistId, StringComparison.OrdinalIgnoreCase);
            var applied = false;
            if (isSteamHuntersSource)
            {
                applied = ApplySteamHuntersMetadata(importedGame, appId);
            }
            else if (isCompletionistSource)
            {
                applied = ApplyCompletionistMetadata(importedGame, appId);
            }

            var storeApplied = false;
            if (applied || ShouldApplySteamStoreFallback(importedGame, appId))
            {
                var includeMedia = isSteamHuntersSource || isCompletionistSource;
                var includeIconFallback = isSteamHuntersSource || isCompletionistSource;
                storeApplied = ApplySteamStoreFallbackMetadata(importedGame, appId, includeMedia, includeIconFallback);
            }

            return applied || storeApplied;
        }

        private bool ApplyFastMetadata(Game importedGame, int appId, LibraryMetadataProvider metadataDownloader)
        {
            if (importedGame == null || appId <= 0 || metadataDownloader == null)
            {
                return false;
            }

            try
            {
                var lookupGame = CreateFastMetadataLookupGame(importedGame, appId);
                var metadata = metadataDownloader.GetMetadata(lookupGame);
                if (metadata == null)
                {
                    return false;
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

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[{_logPrefix}] Failed applying fast metadata for appId={appId}.");
                return false;
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
                _logger?.Debug(ex, $"[{_logPrefix}] Failed applying metadata plugin '{metadataPlugin?.Name}' for appId={appId}.");
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
                        Name = null,
                        Description = FirstNonEmpty(
                            FindTextAfterHeading(document, "Description"),
                            ExtractMetaContent(document, "description")),
                        IconUrl = null,
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
                        Name = null,
                        Description = FirstNonEmpty(
                            ExtractMetaContent(document, "description"),
                            FindTextAfterHeading(document, "Description")),
                        IconUrl = null,
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

            var requestHost = string.Empty;
            if (Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
            {
                requestHost = requestUri.Host ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(requestHost) && _blockedMetadataHosts.Contains(requestHost))
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
            catch (WebException webEx)
            {
                var statusCode = (webEx.Response as HttpWebResponse)?.StatusCode;
                if ((statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.Unauthorized)
                    && !string.IsNullOrWhiteSpace(requestHost))
                {
                    _blockedMetadataHosts.Add(requestHost);
                    _logger?.Debug($"[{_logPrefix}] Marked metadata host as blocked due to {(int) statusCode}: {requestHost}");
                }

                _logger?.Debug(webEx, $"[{_logPrefix}] Failed downloading imported metadata page '{url}'.");
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[{_logPrefix}] Failed downloading imported metadata page '{url}'.");
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

        private bool ApplySteamStoreFallbackMetadata(Game importedGame, int appId, bool includeMedia, bool includeIconFallback)
        {
            if (importedGame == null || appId <= 0)
            {
                return false;
            }

            try
            {
                var applied = false;
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
                        return false;
                    }

                    var root = JObject.Parse(json);
                    var envelope = root[appId.ToString(CultureInfo.InvariantCulture)] as JObject;
                    if (envelope?["success"]?.Value<bool>() != true)
                    {
                        return false;
                    }

                    var data = envelope["data"] as JObject;
                    if (data == null)
                    {
                        return false;
                    }

                    var resolvedName = data["name"]?.Value<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(resolvedName))
                    {
                        applied = applied || !string.Equals(importedGame.Name, resolvedName, StringComparison.Ordinal);
                        importedGame.Name = resolvedName;
                        importedGame.SortingName = resolvedName;
                    }

                    if (string.IsNullOrWhiteSpace(importedGame.Description))
                    {
                        var description = data["about_the_game"]?.Value<string>()?.Trim();
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = data["detailed_description"]?.Value<string>()?.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = data["short_description"]?.Value<string>()?.Trim();
                        }

                        var normalizedDescription = NormalizeMetadataDescription(description);
                        if (!string.Equals(importedGame.Description, normalizedDescription, StringComparison.Ordinal))
                        {
                            applied = true;
                        }

                        importedGame.Description = normalizedDescription;
                    }

                    if (includeMedia || includeIconFallback)
                    {
                        var steamCdnBaseUrl = $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/";
                        var steamAppsBaseUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/";
                        // Prefer true square game icons over store logo banners.
                        var iconUrlCandidates = new List<string>();
                        var steamGridDbIconUrl = TryGetSteamGridDbIconUrl(appId);
                        if (!string.IsNullOrWhiteSpace(steamGridDbIconUrl))
                        {
                            iconUrlCandidates.Add(steamGridDbIconUrl);
                        }

                        var iconHash = data["img_icon_url"]?.Value<string>()?.Trim();
                        var clientIconHash = data["clienticon"]?.Value<string>()?.Trim();
                        AddSteamCommunityIconHashCandidates(iconUrlCandidates, appId, iconHash);
                        AddSteamCommunityIconHashCandidates(iconUrlCandidates, appId, clientIconHash);

                        AddUniqueNonEmpty(iconUrlCandidates, $"{steamCdnBaseUrl}library_600x900_2x.jpg");
                        AddUniqueNonEmpty(iconUrlCandidates, $"{steamCdnBaseUrl}library_600x900.jpg");

                        if (includeIconFallback || includeMedia)
                        {
                            if (string.IsNullOrWhiteSpace(importedGame.Icon))
                            {
                                var iconId = PersistFirstAvailableMetadataFile(importedGame.Id, iconUrlCandidates);
                                if (!string.IsNullOrWhiteSpace(iconId))
                                {
                                    importedGame.Icon = iconId;
                                    applied = true;
                                }
                            }
                        }

                        if (includeMedia)
                        {
                            var coverUrlCandidates = new[]
                            {
                                $"{steamCdnBaseUrl}library_600x900_2x.jpg",
                                $"{steamCdnBaseUrl}library_600x900.jpg"
                            };
                            var backgroundUrlCandidates = new[]
                            {
                                $"{steamAppsBaseUrl}header.jpg",
                                $"{steamCdnBaseUrl}library_hero_2x.jpg",
                                $"{steamCdnBaseUrl}library_hero.jpg",
                                data["background_raw"]?.Value<string>()?.Trim(),
                                data["background"]?.Value<string>()?.Trim()
                            };

                            if (string.IsNullOrWhiteSpace(importedGame.CoverImage))
                            {
                                var coverId = PersistFirstAvailableMetadataFile(importedGame.Id, coverUrlCandidates);
                                if (!string.IsNullOrWhiteSpace(coverId))
                                {
                                    importedGame.CoverImage = coverId;
                                    applied = true;
                                }
                            }

                            if (string.IsNullOrWhiteSpace(importedGame.BackgroundImage))
                            {
                                var backgroundId = PersistFirstAvailableMetadataFile(importedGame.Id, backgroundUrlCandidates);
                                if (!string.IsNullOrWhiteSpace(backgroundId))
                                {
                                    importedGame.BackgroundImage = backgroundId;
                                    applied = true;
                                }
                            }
                        }
                    }

                    var websiteUrl = data["website"]?.Value<string>()?.Trim();
                    var links = importedGame.Links?.ToList() ?? new List<Link>();
                    AddLinkIfMissing(links, "Steam Store", $"https://store.steampowered.com/app/{appId}/");
                    AddLinkIfMissing(links, "Community Hub", $"https://steamcommunity.com/app/{appId}");
                    AddLinkIfMissing(links, "Discussions", $"https://steamcommunity.com/app/{appId}/discussions/");
                    AddLinkIfMissing(links, "Guides", $"https://steamcommunity.com/app/{appId}/guides/");
                    AddLinkIfMissing(links, "News", $"https://store.steampowered.com/news/app/{appId}");

                    var categoryIds = data["categories"]?
                        .Values<JObject>()
                        .Select(category => category?["id"]?.Value<int>() ?? 0)
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
                        applied = applied || importedGame.Links == null || importedGame.Links.Count != links.Count;
                        ReplaceCollection(importedGame.Links, links);
                    }

                    return applied;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[{_logPrefix}] Failed applying Steam Store fallback metadata for appId={appId}.");
                return false;
            }
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

        private static bool IsPlaceholderSteamName(string value, int appId)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return string.Equals(value.Trim(), $"Steam App {appId}", StringComparison.OrdinalIgnoreCase);
        }

        private Game CreateMetadataPluginLookupGame(Game importedGame, int appId, MetadataPlugin metadataPlugin)
        {
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

        private static Game CreateFastMetadataLookupGame(Game importedGame, int appId)
        {
            var lookupName = BuildFastMethodLookupName(importedGame?.Name, appId);
            return new Game
            {
                GameId = appId > 0 ? appId.ToString(CultureInfo.InvariantCulture) : importedGame?.GameId,
                PluginId = SteamDataProvider.SteamPluginId,
                Name = lookupName,
                SortingName = lookupName,
                SourceId = importedGame?.SourceId ?? Guid.Empty
            };
        }

        private static string BuildUniversalSteamMetadataLookupName(string currentName, int appId)
        {
            return appId > 0
                ? appId.ToString(CultureInfo.InvariantCulture)
                : (currentName ?? string.Empty).Trim();
        }

        private static string BuildFastMethodLookupName(string currentName, int appId)
        {
            var trimmedName = (currentName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedName) || IsPlaceholderSteamName(trimmedName, appId))
            {
                return appId.ToString(CultureInfo.InvariantCulture);
            }

            return trimmedName;
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
            importedGame.Features?.Clear();
            importedGame.AgeRatings?.Clear();
            importedGame.Regions?.Clear();
            importedGame.Series?.Clear();
            importedGame.Links?.Clear();
            importedGame.Categories?.Clear();
        }

        private string PersistFirstAvailableMetadataFile(Guid gameId, IEnumerable<string> imageUrls)
        {
            if (gameId == Guid.Empty || imageUrls == null)
            {
                return null;
            }

            foreach (var imageUrl in imageUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()))
            {
                // Quick HEAD check to skip non-existent URLs and avoid wasted download attempts
                if (!UrlExists(imageUrl))
                {
                    continue;
                }

                var fileId = PersistMetadataFile(gameId, new MetadataFile(imageUrl));
                if (!string.IsNullOrWhiteSpace(fileId))
                {
                    return fileId;
                }
            }

            return null;
        }

        private static bool UrlExists(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return false;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.Timeout = 3000;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                request.AllowAutoRedirect = true;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
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
                        ? $"import_meta_{Guid.NewGuid():N}{GetExtensionFromPath(metadataFile.Path)}"
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
                        ? $"import_meta_{Guid.NewGuid():N}{targetExtension}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));

                    using (var webClient = new WebClient())
                    {
                        webClient.DownloadFile(uri, tempFilePath);
                    }

                    return _api.Database.AddFile(tempFilePath, gameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[{_logPrefix}] Failed persisting metadata file for gameId={gameId} from '{metadataFile?.Path}'.");
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
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "ImportedMetadata");
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
                return $"import_meta_{Guid.NewGuid():N}.img";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? $"import_meta_{Guid.NewGuid():N}.img" : sanitized;
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

        private static void AddSteamCommunityIconHashCandidates(ICollection<string> candidates, int appId, string hash)
        {
            if (candidates == null || appId <= 0 || string.IsNullOrWhiteSpace(hash))
            {
                return;
            }

            var normalizedHash = hash.Trim();
            if (normalizedHash.Length != 40 || !normalizedHash.All(Uri.IsHexDigit))
            {
                return;
            }

            var baseUrl = $"https://shared.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{normalizedHash}";
            AddUniqueNonEmpty(candidates, baseUrl + ".ico");
            AddUniqueNonEmpty(candidates, baseUrl + ".png");
            AddUniqueNonEmpty(candidates, baseUrl + ".jpg");
            AddUniqueNonEmpty(candidates, baseUrl + ".jpeg");
            AddUniqueNonEmpty(candidates, baseUrl + ".webp");
        }

        private static void AddUniqueNonEmpty(ICollection<string> values, string value)
        {
            if (values == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim();
            if (values.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            values.Add(normalized);
        }

        private static string PrepareProviderMetadataDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
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

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

        private static string ExtractMetaContent(HtmlDocument document, string propertyName)
        {
            if (document?.DocumentNode == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var node = document.DocumentNode.SelectSingleNode($"//meta[@property='{propertyName}' or @name='{propertyName}']");
            return node?.GetAttributeValue("content", null)?.Trim();
        }

        private static string TryExtractPageTitle(HtmlDocument document)
        {
            return NormalizeMetadataDescription(document?.DocumentNode.SelectSingleNode("//title")?.InnerText);
        }

        private static string ExtractHeadingText(HtmlDocument document)
        {
            var heading = document?.DocumentNode.SelectSingleNode("//h1|//h2");
            return NormalizeMetadataDescription(heading?.InnerText);
        }

        private static string FindTextAfterHeading(HtmlDocument document, string headingText)
        {
            if (document?.DocumentNode == null || string.IsNullOrWhiteSpace(headingText))
            {
                return null;
            }

            var headingNodes = document.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4");
            if (headingNodes == null || headingNodes.Count == 0)
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

        private static string ExtractImageUrl(HtmlDocument document, string pageUrl, string xpath)
        {
            var source = document?.DocumentNode.SelectSingleNode(xpath)?.GetAttributeValue("src", null)?.Trim();
            return ResolveAbsoluteUrl(pageUrl, source);
        }

        private static string ResolveAbsoluteUrl(string pageUrl, string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            if (Uri.TryCreate(source, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
            {
                return source;
            }

            return Uri.TryCreate(baseUri, source, out var combinedUri)
                ? combinedUri.ToString()
                : source;
        }

        private static string BuildRequestReferer(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            return uri.GetLeftPart(UriPartial.Authority) + "/";
        }

        private string TryGetSteamGridDbIconUrl(int appId)
        {
            if (appId <= 0)
            {
                return null;
            }

            if (_steamGridDbIconUrlCache.TryGetValue(appId, out var cachedUrl))
            {
                if (!string.IsNullOrWhiteSpace(cachedUrl))
                {
                    return cachedUrl;
                }

                _steamGridDbIconUrlCache.Remove(appId);
            }

            var steamCommunityFallback = TryGetSteamCommunityAppIconUrl(appId);
            if (!string.IsNullOrWhiteSpace(steamCommunityFallback))
            {
                _steamGridDbIconUrlCache[appId] = steamCommunityFallback;
            }

            return steamCommunityFallback;
        }

        private string TryGetSteamCommunityAppIconUrl(int appId)
        {
            if (appId <= 0)
            {
                return null;
            }

            int maxAttempts;
            lock (_steamCommunityIconRequestLock)
            {
                maxAttempts = _steamCommunityMaxAttempts;
            }

            var rateLimitedEncountered = false;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (!ThrottleSteamCommunityIconRequest())
                    {
                        lock (_steamCommunityIconRequestLock)
                        {
                            _steamCommunityRateLimitedMissAppIds.Add(appId);
                        }

                        return null;
                    }

                    var requestUrl = $"https://steamcommunity.com/app/{appId}?l=english";
                    using (var webClient = new WebClient())
                    {
                        webClient.Headers[HttpRequestHeader.UserAgent] =
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
                        webClient.Headers[HttpRequestHeader.Accept] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                        webClient.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
                        webClient.Encoding = Encoding.UTF8;

                        var html = webClient.DownloadString(requestUrl);
                        if (string.IsNullOrWhiteSpace(html))
                        {
                            if (rateLimitedEncountered)
                            {
                                lock (_steamCommunityIconRequestLock)
                                {
                                    _steamCommunityRateLimitedMissAppIds.Add(appId);
                                }
                            }

                            return null;
                        }

                        var iconRegex = new Regex(
                            $@"(?:https?:)?//(?:cdn\.akamai\.steamstatic\.com|cdn\.cloudflare\.steamstatic\.com|media\.steampowered\.com|cdn\.fastly\.steamstatic\.com|community\.fastly\.steamstatic\.com)/steamcommunity/public/images/apps/{appId}/[a-fA-F0-9]{{40}}\.(?:jpg|jpeg|png|webp|ico)(?:\?[^\""""'<>\s]*)?",
                            RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        var match = iconRegex.Match(html);
                        var iconUrl = match.Success ? match.Value?.Trim() : null;
                        if (string.IsNullOrWhiteSpace(iconUrl))
                        {
                            if (rateLimitedEncountered)
                            {
                                lock (_steamCommunityIconRequestLock)
                                {
                                    _steamCommunityRateLimitedMissAppIds.Add(appId);
                                }
                            }

                            return null;
                        }

                        if (iconUrl.IndexOf($"/steamcommunity/public/images/apps/{appId}/", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            if (rateLimitedEncountered)
                            {
                                lock (_steamCommunityIconRequestLock)
                                {
                                    _steamCommunityRateLimitedMissAppIds.Add(appId);
                                }
                            }

                            return null;
                        }

                        lock (_steamCommunityIconRequestLock)
                        {
                            _steamCommunityConsecutive429Count = 0;
                            _steamCommunityRateLimitedMissAppIds.Remove(appId);
                        }

                        _logger?.Debug($"[{_logPrefix}] Steam community app icon resolved for appId={appId}: {iconUrl}");
                        _steamGridDbIconUrlCache[appId] = iconUrl;
                        return iconUrl;
                    }
                }
                catch (WebException webEx)
                {
                    var response = webEx.Response as HttpWebResponse;
                    if (response?.StatusCode == (HttpStatusCode) 429)
                    {
                        rateLimitedEncountered = true;
                        TimeSpan retryDelay;
                        var attemptBasedFallback = TimeSpan.FromSeconds(3 + (attempt * 3));

                        lock (_steamCommunityIconRequestLock)
                        {
                            _steamCommunityConsecutive429Count = Math.Min(_steamCommunityConsecutive429Count + 1, 6);
                            var consecutivePenalty = TimeSpan.FromSeconds(_steamCommunityConsecutive429Count * 2);
                            retryDelay = ReadRetryAfterDelay(response, attemptBasedFallback + consecutivePenalty);
                            var nextAllowed = DateTime.UtcNow.Add(retryDelay);
                            if (nextAllowed > _steamCommunityIconBackoffUntilUtc)
                            {
                                _steamCommunityIconBackoffUntilUtc = nextAllowed;
                            }
                        }

                        _logger?.Debug($"[{_logPrefix}] Steam community app icon rate-limited for appId={appId}; attempt={attempt}/{maxAttempts}; backing off for {retryDelay.TotalSeconds:0.##}s.");

                        if (attempt < maxAttempts)
                        {
                            continue;
                        }

                        lock (_steamCommunityIconRequestLock)
                        {
                            _steamCommunityRateLimitedMissAppIds.Add(appId);
                        }

                        _logger?.Debug(webEx, $"[{_logPrefix}] Steam community app icon lookup failed for appId={appId} after {maxAttempts} attempts.");
                        return null;
                    }

                    if (rateLimitedEncountered)
                    {
                        lock (_steamCommunityIconRequestLock)
                        {
                            _steamCommunityRateLimitedMissAppIds.Add(appId);
                        }
                    }

                    _logger?.Debug(webEx, $"[{_logPrefix}] Steam community app icon lookup failed for appId={appId}.");
                    return null;
                }
                catch (Exception ex)
                {
                    if (rateLimitedEncountered)
                    {
                        lock (_steamCommunityIconRequestLock)
                        {
                            _steamCommunityRateLimitedMissAppIds.Add(appId);
                        }
                    }

                    _logger?.Debug(ex, $"[{_logPrefix}] Steam community app icon lookup failed for appId={appId}.");
                    return null;
                }
            }

            return null;
        }

        private bool ThrottleSteamCommunityIconRequest()
        {
            TimeSpan waitDuration = TimeSpan.Zero;
            var shouldWait = true;
            var hasActiveBackoff = false;
            lock (_steamCommunityIconRequestLock)
            {
                var now = DateTime.UtcNow;
                var minIntervalMs = 350 + (_steamCommunityConsecutive429Count * 350);
                var minNextRequest = _steamCommunityIconLastRequestUtc == DateTime.MinValue
                    ? now
                    : _steamCommunityIconLastRequestUtc.AddMilliseconds(minIntervalMs);

                hasActiveBackoff = _steamCommunityIconBackoffUntilUtc > now;
                if (hasActiveBackoff)
                {
                    waitDuration = _steamCommunityIconBackoffUntilUtc - now;
                }
                else if (minNextRequest > now)
                {
                    waitDuration = minNextRequest - now;
                }

                shouldWait = _steamCommunityWaitDuringBackoff;
            }

            if (hasActiveBackoff && waitDuration > TimeSpan.Zero && !shouldWait)
            {
                return false;
            }

            if (waitDuration > TimeSpan.Zero)
            {
                System.Threading.Thread.Sleep(waitDuration);
            }

            lock (_steamCommunityIconRequestLock)
            {
                _steamCommunityIconLastRequestUtc = DateTime.UtcNow;
            }

            return true;
        }

        private static TimeSpan ReadRetryAfterDelay(HttpWebResponse response, TimeSpan fallbackDelay)
        {
            try
            {
                var retryAfter = response?.Headers?["Retry-After"];
                if (string.IsNullOrWhiteSpace(retryAfter))
                {
                    return fallbackDelay;
                }

                if (int.TryParse(retryAfter.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retryAfterUtc))
                {
                    var delta = retryAfterUtc.UtcDateTime - DateTime.UtcNow;
                    if (delta > TimeSpan.Zero)
                    {
                        return delta;
                    }
                }
            }
            catch
            {
            }

            return fallbackDelay;
        }
    }
}