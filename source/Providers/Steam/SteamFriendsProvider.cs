using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Providers.Steam.Models;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamFriendsProvider : IFriendsProvider
    {
        private readonly SteamHttpClient _steamClient;
        private readonly SteamApiClient _steamApiClient;
        private readonly SteamScanner _scanner;
        private readonly SteamWebApiTokenResolver _tokenResolver;
        private readonly SteamHuntersCategoryEnricher _steamHuntersCategoryEnricher;
        private readonly SteamSessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly object _refreshStateLock = new object();
        private TokenState _preparedRefreshTokenState;
        private IDisposable _refreshViewLease;
        private Dictionary<int, Task<SchemaAndPercentages>> _friendRefreshSchemaTasks =
            new Dictionary<int, Task<SchemaAndPercentages>>();

        public SteamFriendsProvider(
            SteamHttpClient steamClient,
            SteamApiClient steamApiClient,
            SteamScanner scanner,
            SteamWebApiTokenResolver tokenResolver,
            SteamHuntersCategoryEnricher steamHuntersCategoryEnricher,
            SteamSessionManager sessionManager,
            ILogger logger)
        {
            _steamClient = steamClient ?? throw new ArgumentNullException(nameof(steamClient));
            _steamApiClient = steamApiClient ?? throw new ArgumentNullException(nameof(steamApiClient));
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _tokenResolver = tokenResolver ?? throw new ArgumentNullException(nameof(tokenResolver));
            _steamHuntersCategoryEnricher = steamHuntersCategoryEnricher;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger;
        }

        public string ProviderKey => "Steam";

        public async Task<FriendsProviderResult<FriendsRefreshPreparation>> BeginRefreshAsync(CancellationToken cancel)
        {
            EndRefresh();
            _steamHuntersCategoryEnricher?.ClearCache();

            // Hold one shared offscreen CEF view for the whole friend refresh (released in
            // EndRefresh) so per-friend page fetches that fall back to CEF reuse a single
            // browser instance instead of creating one per page.
            lock (_refreshStateLock)
            {
                _refreshViewLease = _sessionManager.BeginOffscreenViewLease();
            }

            var session = await ResolveSteamSessionFromTokenResolverAsync(cancel).ConfigureAwait(false);
            if (!session.IsSuccess)
            {
                EndRefresh();
                return FriendsProviderResult<FriendsRefreshPreparation>.Failed(
                    session.ErrorMessage,
                    authRequired: session.AuthRequired);
            }

            lock (_refreshStateLock)
            {
                _preparedRefreshTokenState = session;
                _friendRefreshSchemaTasks = new Dictionary<int, Task<SchemaAndPercentages>>();
            }

            return FriendsProviderResult<FriendsRefreshPreparation>.FromData(new FriendsRefreshPreparation
            {
                CanRefreshAchievements = !string.IsNullOrWhiteSpace(session.WebApiToken)
            });
        }

        public void EndRefresh()
        {
            IDisposable viewLease;
            lock (_refreshStateLock)
            {
                _preparedRefreshTokenState = null;
                _friendRefreshSchemaTasks.Clear();
                viewLease = _refreshViewLease;
                _refreshViewLease = null;
            }

            viewLease?.Dispose();
        }

        public async Task<FriendsProviderResult<IReadOnlyList<FriendIdentity>>> GetFriendsAsync(CancellationToken cancel)
        {
            var session = await ResolveSteamSessionAsync(cancel).ConfigureAwait(false);
            if (!session.IsSuccess)
            {
                return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.Failed(
                    session.ErrorMessage,
                    authRequired: session.AuthRequired);
            }

            return await GetFriendsFromCommunityPageAsync(session.SteamUserId, cancel).ConfigureAwait(false);
        }

        public async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetOwnedGamesAsync(
            FriendIdentity friend,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(friend?.ExternalUserId))
            {
                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.Failed("Steam friend id is missing.");
            }

            var gamesResult = await GetOwnedGamesFromCommunityPageAsync(friend.ExternalUserId, cancel).ConfigureAwait(false);
            if (gamesResult?.Success != true)
            {
                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.Failed(
                    gamesResult?.ErrorMessage ??
                    $"Steam owned games are unavailable from the community page for friend {friend.ExternalUserId}.",
                    authRequired: gamesResult?.AuthRequired == true,
                    transientFailure: gamesResult?.TransientFailure == true);
            }

            // Steam friend refresh relies on the rendered community games page's
            // "ACHIEVEMENTS X/Y" marker. If that count is missing, treat the game as not
            // supporting achievements and do not let it enter definition or unlock refresh work.
            var achievementGames = (gamesResult.Games ?? Array.Empty<SteamOwnedGame>())
                .Where(HasSteamAchievementProgressSignal)
                .ToList();
            var skipped = (gamesResult.Games?.Count ?? 0) - achievementGames.Count;
            if (skipped > 0)
            {
                _logger?.Debug(
                    $"Skipped {skipped} Steam friend owned game(s) without an achievement progress signal for friend {friend.ExternalUserId}.");
            }

            var result = achievementGames
                .Select(game => new FriendGameOwnership
                {
                    ProviderKey = ProviderKey,
                    ExternalUserId = friend.ExternalUserId,
                    AppId = game.AppId,
                    GameName = FirstNonEmpty(game.Name, $"Steam App {game.AppId}"),
                    IconUrl = SteamImageUrls.Icon(game.AppId),
                    CoverUrl = SteamImageUrls.Cover(game.AppId),
                    PlaytimeForeverMinutes = Math.Max(0, game.PlaytimeForever),
                    Playtime2WeeksMinutes = game.Playtime2Weeks.HasValue ? Math.Max(0, game.Playtime2Weeks.Value) : (int?)null,
                    LastPlayedUtc = ToUtc(game.LastPlayedUnixSeconds),
                    AchievementUnlocksHint = game.AchievementsEarned,
                    AchievementTotalHint = game.AchievementsTotal
                })
                .ToList();

            return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(result);
        }

        private static bool HasSteamAchievementProgressSignal(SteamOwnedGame game)
        {
            if (game?.AppId <= 0)
            {
                return false;
            }

            return game.AchievementsEarned.HasValue &&
                   game.AchievementsTotal.HasValue &&
                   game.AchievementsTotal.Value > 0;
        }

        private async Task<FriendsProviderResult<IReadOnlyList<FriendIdentity>>> GetFriendsFromCommunityPageAsync(
            string selfSteamId64,
            CancellationToken cancel)
        {
            SteamPageResult page;
            try
            {
                page = await _steamClient.GetFriendsPageAsync(selfSteamId64, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Steam community friends page request failed.");
                return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.Failed(
                    "Steam community friends page request failed.",
                    transientFailure: true);
            }

            if (!IsUsableCommunityPage(page))
            {
                return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.Failed(
                    "Steam friend list is unavailable from the community page.",
                    authRequired: LooksLoggedOut(page));
            }

            var parsedFriends = SteamCommunityPageParser.ParseFriends(page.Html);
            if (parsedFriends.Count == 0 && !SteamCommunityPageParser.LooksLikeFriendsPayload(page.Html))
            {
                return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.Failed(
                    "Steam community friends page did not contain a friends payload.");
            }

            var now = DateTime.UtcNow;
            var friends = parsedFriends
                .Where(friend => !string.IsNullOrWhiteSpace(friend?.SteamId))
                .Select(friend => new FriendIdentity
                {
                    ProviderKey = ProviderKey,
                    ExternalUserId = friend.SteamId.Trim(),
                    DisplayName = FirstNonEmpty(friend.DisplayName, friend.SteamId),
                    AvatarUrl = friend.AvatarUrl,
                    LastRefreshedUtc = now
                })
                .ToList();

            return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.FromData(friends);
        }

        private async Task<OwnedGamesPageResult> GetOwnedGamesFromCommunityPageAsync(
            string steamId64,
            CancellationToken cancel)
        {
            SteamPageResult page;
            try
            {
                page = await _steamClient.GetOwnedGamesPageAsync(steamId64, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Steam community games page request failed for friend {steamId64}.");
                return OwnedGamesPageResult.Failed(
                    $"Steam community games page request failed for friend {steamId64}.",
                    transientFailure: true);
            }

            if (page == null || string.IsNullOrWhiteSpace(page.Html))
            {
                return OwnedGamesPageResult.Failed(
                    $"Steam community games page returned no content for friend {steamId64}.",
                    transientFailure: true);
            }

            if (LooksLoggedOut(page))
            {
                return OwnedGamesPageResult.Failed(
                    $"Steam community games page requires Steam web authentication for friend {steamId64}.",
                    authRequired: true);
            }

            if (SteamHttpClient.LooksPrivateOrRestrictedStatsPayload(page.Html, page.FinalUrl))
            {
                return OwnedGamesPageResult.Failed(
                    $"Steam community games page is private or restricted for friend {steamId64}.");
            }

            if (!SteamCommunityPageParser.LooksLikeOwnedGamesPayload(page.Html))
            {
                var profilePageResult = await GetOwnedGamesFromProfileXmlPageAsync(steamId64, cancel).ConfigureAwait(false);
                if (profilePageResult?.Success == true)
                {
                    return profilePageResult;
                }

                return OwnedGamesPageResult.Failed(
                    $"Steam community games page did not contain an owned-games payload for friend {steamId64}. " +
                    $"GamesPage={BuildPageSignature(page)}; ProfileXml={profilePageResult?.ErrorMessage ?? "not checked"}");
            }

            var games = SteamCommunityPageParser.ParseOwnedGames(page.Html);
            if (games.Count > 0)
            {
                _logger?.Debug(
                    $"Steam community owned games parsed for friend {steamId64}: " +
                    $"renderedGamesPage={games.Count}.");
                return OwnedGamesPageResult.FromGames(games);
            }

            var fallbackProfilePageResult = await GetOwnedGamesFromProfileXmlPageAsync(steamId64, cancel).ConfigureAwait(false);
            if (fallbackProfilePageResult?.Success == true)
            {
                return fallbackProfilePageResult;
            }

            return OwnedGamesPageResult.Failed(
                $"Steam community games page contained owned-games markers but parsed zero games for friend {steamId64}. " +
                $"GamesPage={BuildPageSignature(page)}; ProfileXml={fallbackProfilePageResult?.ErrorMessage ?? "not checked"}");
        }

        private async Task<OwnedGamesPageResult> GetOwnedGamesFromProfileXmlPageAsync(
            string steamId64,
            CancellationToken cancel)
        {
            SteamPageResult page;
            try
            {
                page = await _steamClient.GetProfileXmlPageAsync(steamId64, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Steam profile XML page request failed for friend {steamId64}.");
                return OwnedGamesPageResult.Failed(
                    $"ProfileXml=request failed",
                    transientFailure: true);
            }

            if (page == null || string.IsNullOrWhiteSpace(page.Html))
            {
                return OwnedGamesPageResult.Failed("ProfileXml=empty");
            }

            if (LooksLoggedOut(page))
            {
                return OwnedGamesPageResult.Failed("ProfileXml=auth required", authRequired: true);
            }

            if (!SteamCommunityPageParser.LooksLikeOwnedGamesPayload(page.Html))
            {
                return OwnedGamesPageResult.Failed(BuildPageSignature(page));
            }

            return OwnedGamesPageResult.FromGames(SteamCommunityPageParser.ParseOwnedGames(page.Html));
        }

        public async Task<FriendsProviderResult<FriendGameAchievements>> GetFriendGameAchievementsAsync(
            FriendIdentity friend,
            string providerGameKey,
            int appId,
            string gameName,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(friend?.ExternalUserId) || appId <= 0)
            {
                return FriendsProviderResult<FriendGameAchievements>.Failed("Steam friend id or app id is missing.");
            }

            var token = await ResolveTokenAsync(cancel).ConfigureAwait(false);
            if (!token.IsSuccess)
            {
                return FriendsProviderResult<FriendGameAchievements>.Failed(
                    token.ErrorMessage,
                    authRequired: token.AuthRequired);
            }

            AchievementsScrapeResponse scrape;
            try
            {
                scrape = await _scanner.ScrapeAchievementsAsync(
                    friend.ExternalUserId,
                    appId,
                    token.WebApiToken,
                    cancel,
                    includeLocked: true,
                    gameName: gameName).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Steam friend achievement scrape failed for friend={friend.ExternalUserId}, appId={appId}.");
                return FriendsProviderResult<FriendGameAchievements>.Failed(
                    "Steam friend achievement scrape failed.",
                    transientFailure: true);
            }

            // Reconstruct stable api names from the schema's icon hashes so friend rows match the
            // canonical definitions by language-independent key rather than localized display text.
            // A schema failure degrades gracefully: rows keep a null ApiName and the merge falls
            // back to display-name/icon matching.
            var apiNamesByRow = await ResolveScrapedApiNamesAsync(token.WebApiToken, appId, scrape?.Rows, cancel)
                .ConfigureAwait(false);

            var data = new FriendGameAchievements
            {
                Friend = friend,
                AppId = appId,
                LastUpdatedUtc = DateTime.UtcNow,
                StatsUnavailable = scrape?.StatsUnavailable == true,
                TransientFailure = scrape?.TransientFailure == true,
                DetailCode = scrape?.DetailCode ?? SteamScrapeDetail.None,
                Rows = MapRows(scrape?.Rows, apiNamesByRow)
            };

            if (data.TransientFailure)
            {
                var failed = FriendsProviderResult<FriendGameAchievements>.Failed(
                    scrape?.Detail ?? "Steam scrape returned a transient failure.",
                    transientFailure: true);
                failed.Data = data;
                return failed;
            }

            return FriendsProviderResult<FriendGameAchievements>.FromData(data);
        }

        public async Task<FriendsProviderResult<FriendGameDefinition>> GetFriendGameDefinitionAsync(
            string providerGameKey,
            int appId,
            string gameName,
            CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return FriendsProviderResult<FriendGameDefinition>.Failed("Steam app id is missing.");
            }

            var token = await ResolveTokenAsync(cancel).ConfigureAwait(false);
            if (!token.IsSuccess)
            {
                return FriendsProviderResult<FriendGameDefinition>.Failed(
                    token.ErrorMessage,
                    authRequired: token.AuthRequired);
            }

            try
            {
                var schema = await _steamApiClient
                    .GetSchemaForGameDetailedAsync(token.WebApiToken, appId, "english", cancel)
                    .ConfigureAwait(false);
                var achievements = MapSchemaAchievements(schema);
                await EnrichSteamHuntersCategoriesAsync(appId, gameName, achievements, cancel).ConfigureAwait(false);

                var status = achievements.Count > 0
                    ? FriendGameDefinitionStatus.Ok
                    : await ResolveEmptySchemaStatusAsync(token.WebApiToken, appId, cancel).ConfigureAwait(false);

                return FriendsProviderResult<FriendGameDefinition>.FromData(new FriendGameDefinition
                {
                    ProviderKey = ProviderKey,
                    AppId = appId,
                    GameName = FirstNonEmpty(gameName, $"Steam App {appId}"),
                    Status = status,
                    LastCheckedUtc = DateTime.UtcNow,
                    Achievements = achievements
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Steam game definition fetch failed for appId={appId}.");
                return FriendsProviderResult<FriendGameDefinition>.Failed(
                    "Steam game definition fetch failed.",
                    transientFailure: true);
            }
        }

        private Task EnrichSteamHuntersCategoriesAsync(
            int appId,
            string gameName,
            IList<AchievementDetail> achievements,
            CancellationToken cancel)
        {
            if (_steamHuntersCategoryEnricher == null ||
                !ShouldUseSteamHuntersForCategories())
            {
                return Task.CompletedTask;
            }

            // Friends refreshes never download default category art (playniteGameId: null):
            // provider-only friend games have no Playnite id to key art files by, and mapped
            // games get their art from the owner's own library scan. The category labels/types
            // set here do persist for mapped games (seed/backfill at definition-save time).
            return _steamHuntersCategoryEnricher.EnrichAsync(appId, gameName, achievements, playniteGameId: null, cancel);
        }

        private static bool ShouldUseSteamHuntersForCategories()
        {
            return ProviderRegistry.Settings<SteamSettings>()?.UseSteamHuntersForCategories == true;
        }

        private static bool IsUsableCommunityPage(SteamPageResult page)
        {
            if (page == null || string.IsNullOrWhiteSpace(page.Html))
            {
                return false;
            }

            if (LooksLoggedOut(page))
            {
                return false;
            }

            return !SteamHttpClient.LooksPrivateOrRestrictedStatsPayload(page.Html, page.FinalUrl);
        }

        private static bool LooksLoggedOut(SteamPageResult page)
        {
            return SteamHttpClient.LooksUnauthenticatedSteamPage(page?.Html, page?.FinalUrl);
        }

        private static string BuildPageSignature(SteamPageResult page)
        {
            var html = page?.Html ?? string.Empty;
            var markers = new List<string>();
            foreach (var marker in new[]
            {
                "<gamesList",
                "webkit-xml-viewer-source-xml",
                "&lt;gamesList",
                "<mostPlayedGames",
                "&lt;mostPlayedGames",
                "rgGames",
                "games_list_rows",
                "gameListRow",
                "gameslistitems_",
                "window.SSR.loaderData",
                "window.SSR.renderContext",
                "OwnedGames",
                "playtime_forever",
                "/app/",
                "/apps/",
                "data-app",
                "appid",
                "TOTAL PLAYED",
                "ACHIEVEMENTS",
                "Sign In",
                "profile_private_info",
                "This profile is private"
            })
            {
                if (html.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    markers.Add(marker);
                }
            }

            return $"Status={(int)(page?.StatusCode ?? 0)}, FinalUrl={page?.FinalUrl}, Len={html.Length}, " +
                   $"Title={ExtractTitle(html)}, Markers={string.Join(",", markers)}";
        }

        private static string ExtractTitle(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var start = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += "<title>".Length;
            var end = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
            if (end <= start)
            {
                return string.Empty;
            }

            var title = html.Substring(start, end - start);
            title = string.Join(" ", title.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return title.Length <= 80 ? title : title.Substring(0, 80);
        }

        private async Task<TokenState> ResolveTokenAsync(CancellationToken cancel)
        {
            var session = await ResolveSteamSessionAsync(cancel).ConfigureAwait(false);
            if (!session.IsSuccess || string.IsNullOrWhiteSpace(session.WebApiToken))
            {
                return new TokenState
                {
                    IsSuccess = false,
                    AuthRequired = true,
                    ErrorMessage = "Steam web API token could not be resolved."
                };
            }

            return session;
        }

        private async Task<TokenState> ResolveSteamSessionAsync(CancellationToken cancel)
        {
            lock (_refreshStateLock)
            {
                if (_preparedRefreshTokenState != null)
                {
                    return _preparedRefreshTokenState;
                }
            }

            return await ResolveSteamSessionFromTokenResolverAsync(cancel).ConfigureAwait(false);
        }

        private async Task<TokenState> ResolveSteamSessionFromTokenResolverAsync(CancellationToken cancel)
        {
            var resolution = await _tokenResolver.ResolveAsync(cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolution?.UserId))
            {
                return new TokenState
                {
                    IsSuccess = false,
                    AuthRequired = true,
                    ErrorMessage = "Steam web session could not be resolved."
                };
            }

            return new TokenState
            {
                IsSuccess = true,
                WebApiToken = resolution.Token?.Trim(),
                SteamUserId = resolution.UserId.Trim()
            };
        }

        private async Task<IReadOnlyDictionary<ScrapedAchievement, string>> ResolveScrapedApiNamesAsync(
            string accessToken,
            int appId,
            IReadOnlyCollection<ScrapedAchievement> rows,
            CancellationToken cancel)
        {
            if (rows == null || rows.Count == 0 || string.IsNullOrWhiteSpace(accessToken))
            {
                return new Dictionary<ScrapedAchievement, string>();
            }

            try
            {
                var schema = await FetchFriendRefreshSchemaAsync(accessToken, appId, cancel).ConfigureAwait(false);
                return SteamAchievementApiNameResolver.Resolve(schema, rows);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RemoveCachedFriendRefreshSchemaTask(appId);
                _logger?.Debug(ex, $"Steam friend achievement api-name reconstruction failed for appId={appId}.");
                return new Dictionary<ScrapedAchievement, string>();
            }
        }

        private Task<SchemaAndPercentages> FetchFriendRefreshSchemaAsync(
            string accessToken,
            int appId,
            CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return _scanner.FetchSchemaAsync(accessToken, appId, cancel);
            }

            lock (_refreshStateLock)
            {
                if (_friendRefreshSchemaTasks == null)
                {
                    _friendRefreshSchemaTasks = new Dictionary<int, Task<SchemaAndPercentages>>();
                }

                if (!_friendRefreshSchemaTasks.TryGetValue(appId, out var task))
                {
                    task = _scanner.FetchSchemaAsync(accessToken, appId, cancel);
                    _friendRefreshSchemaTasks[appId] = task;
                }

                return task;
            }
        }

        private void RemoveCachedFriendRefreshSchemaTask(int appId)
        {
            if (appId <= 0)
            {
                return;
            }

            lock (_refreshStateLock)
            {
                _friendRefreshSchemaTasks?.Remove(appId);
            }
        }

        private static List<FriendAchievementRow> MapRows(
            IEnumerable<ScrapedAchievement> rows,
            IReadOnlyDictionary<ScrapedAchievement, string> apiNamesByRow)
        {
            return rows?
                .Where(row => row != null)
                .Select(row => new FriendAchievementRow
                {
                    ApiName = apiNamesByRow != null && apiNamesByRow.TryGetValue(row, out var apiName)
                        ? apiName
                        : null,
                    DisplayName = row.DisplayName,
                    Description = row.Description,
                    IconUrl = row.IconUrl,
                    Unlocked = row.IsUnlocked,
                    UnlockTimeUtc = row.UnlockTimeUtc,
                    ProgressNum = row.ProgressNum,
                    ProgressDenom = row.ProgressDenom
                })
                .ToList() ?? new List<FriendAchievementRow>();
        }

        private static List<AchievementDetail> MapSchemaAchievements(SchemaAndPercentages schema)
        {
            return schema?.Achievements?
                .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.Name))
                .Select(achievement =>
                {
                    var normalizedPercent = schema.GlobalPercentages?.TryGetValue(achievement.Name, out var percent) == true
                        ? NormalizePercent(percent)
                        : null;
                    return new AchievementDetail
                    {
                        ApiName = achievement.Name,
                        DisplayName = !string.IsNullOrWhiteSpace(achievement.DisplayName)
                            ? achievement.DisplayName
                            : achievement.Name,
                        Description = achievement.Description ?? string.Empty,
                        UnlockedIconPath = achievement.Icon,
                        LockedIconPath = achievement.IconGray,
                        Hidden = achievement.Hidden == 1,
                        GlobalPercentUnlocked = normalizedPercent,
                        Rarity = normalizedPercent.HasValue
                            ? PercentRarityHelper.GetRarityTier(normalizedPercent.Value)
                            : (achievement.Hidden == 1 ? RarityTier.Rare : RarityTier.Common),
                        Unlocked = false
                    };
                })
                .ToList() ?? new List<AchievementDetail>();
        }

        private async Task<FriendGameDefinitionStatus> ResolveEmptySchemaStatusAsync(
            string token,
            int appId,
            CancellationToken cancel)
        {
            var hasAchievements = await _steamApiClient
                .GetGameHasAchievementsAsync(token, appId, "english", cancel)
                .ConfigureAwait(false);
            if (hasAchievements == false)
            {
                return FriendGameDefinitionStatus.NoAchievements;
            }

            return FriendGameDefinitionStatus.Unavailable;
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (value < 0)
            {
                return 0;
            }

            return value > 100 ? 100 : value;
        }

        private static DateTime? ToUtc(long? unixSeconds)
        {
            if (!unixSeconds.HasValue || unixSeconds.Value <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime;
            }
            catch
            {
                return null;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

        private sealed class TokenState
        {
            public bool IsSuccess { get; set; }
            public bool AuthRequired { get; set; }
            public string ErrorMessage { get; set; }
            public string WebApiToken { get; set; }
            public string SteamUserId { get; set; }
        }

        private sealed class OwnedGamesPageResult
        {
            public bool Success { get; private set; }
            public bool AuthRequired { get; private set; }
            public bool TransientFailure { get; private set; }
            public string ErrorMessage { get; private set; }
            public IReadOnlyList<SteamOwnedGame> Games { get; private set; }

            public static OwnedGamesPageResult FromGames(IReadOnlyList<SteamOwnedGame> games)
            {
                return new OwnedGamesPageResult
                {
                    Success = true,
                    Games = games ?? Array.Empty<SteamOwnedGame>()
                };
            }

            public static OwnedGamesPageResult Failed(
                string message,
                bool authRequired = false,
                bool transientFailure = false)
            {
                return new OwnedGamesPageResult
                {
                    Success = false,
                    ErrorMessage = message,
                    AuthRequired = authRequired,
                    TransientFailure = transientFailure
                };
            }
        }
    }
}
