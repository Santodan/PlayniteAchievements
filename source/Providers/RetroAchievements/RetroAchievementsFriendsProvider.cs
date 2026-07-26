using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsFriendsProvider : IFriendsProvider
    {
        private const string Provider = "RetroAchievements";
        private const int PageSize = 500;

        private readonly ILogger _logger;
        private readonly Func<RetroAchievementsApiClient> _apiResolver;
        private readonly Func<RetroAchievementsHashIndexStore> _hashIndexResolver;

        public RetroAchievementsFriendsProvider(
            ILogger logger,
            Func<RetroAchievementsApiClient> apiResolver,
            Func<RetroAchievementsHashIndexStore> hashIndexResolver)
        {
            _logger = logger;
            _apiResolver = apiResolver ?? throw new ArgumentNullException(nameof(apiResolver));
            _hashIndexResolver = hashIndexResolver ?? throw new ArgumentNullException(nameof(hashIndexResolver));
        }

        public string ProviderKey => Provider;

        public Task<FriendsProviderResult<FriendsRefreshPreparation>> BeginRefreshAsync(CancellationToken cancel)
        {
            var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            if (string.IsNullOrWhiteSpace(providerSettings?.RaUsername) ||
                string.IsNullOrWhiteSpace(providerSettings?.RaWebApiKey))
            {
                return Task.FromResult(FriendsProviderResult<FriendsRefreshPreparation>.Failed(
                    "RetroAchievements credentials are not configured.",
                    authRequired: true));
            }

            try
            {
                _ = _apiResolver();
                return Task.FromResult(FriendsProviderResult<FriendsRefreshPreparation>.FromData(
                    new FriendsRefreshPreparation { CanRefreshAchievements = true }));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[RAFriends] Failed to initialize RetroAchievements API client.");
                return Task.FromResult(FriendsProviderResult<FriendsRefreshPreparation>.Failed(
                    "RetroAchievements API client could not be initialized.",
                    transientFailure: true));
            }
        }

        public void EndRefresh()
        {
        }

        public async Task<FriendsProviderResult<IReadOnlyList<FriendIdentity>>> GetFriendsAsync(CancellationToken cancel)
        {
            try
            {
                var api = _apiResolver();
                var now = DateTime.UtcNow;
                var followedUsers = await FetchFollowedUsersAsync(api, cancel).ConfigureAwait(false);
                var friends = followedUsers
                    .Where(user => user != null &&
                                   (!string.IsNullOrWhiteSpace(user.ULID) ||
                                    !string.IsNullOrWhiteSpace(user.User)))
                    .Select(user =>
                    {
                        var username = user.User?.Trim();
                        var id = !string.IsNullOrWhiteSpace(user.ULID)
                            ? user.ULID.Trim()
                            : username;
                        return new FriendIdentity
                        {
                            ProviderKey = Provider,
                            ExternalUserId = id,
                            DisplayName = string.IsNullOrWhiteSpace(username) ? id : username,
                            AvatarUrl = RetroAchievementsAchievementMapper.BuildAvatarUrl(username),
                            LastRefreshedUtc = now
                        };
                    })
                    .GroupBy(friend => friend.ExternalUserId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.FromData(friends);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[RAFriends] Failed to fetch followed RetroAchievements users.");
                return FriendsProviderResult<IReadOnlyList<FriendIdentity>>.Failed(
                    "RetroAchievements followed users could not be fetched.",
                    transientFailure: true);
            }
        }

        public async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> GetOwnedGamesAsync(
            FriendIdentity friend,
            CancellationToken cancel)
        {
            var friendId = friend?.ExternalUserId?.Trim();
            if (string.IsNullOrWhiteSpace(friendId))
            {
                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.Failed("RetroAchievements friend id is missing.");
            }

            try
            {
                var api = _apiResolver();
                var progress = await FetchCompletionProgressAsync(api, friendId, cancel).ConfigureAwait(false);
                var rows = await BuildOwnedGameRowsAsync(friendId, progress, cancel).ConfigureAwait(false);
                rows = rows
                    .OrderByDescending(item => item.LastPlayedUtc ?? DateTime.MinValue)
                    .ThenBy(item => item.GameName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(rows);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RAFriends] Failed to fetch completion progress for friend={friendId}.");
                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.Failed(
                    "RetroAchievements completion progress could not be fetched.",
                    transientFailure: true);
            }
        }

        public async Task<FriendsProviderResult<FriendGameAchievements>> GetFriendGameAchievementsAsync(
            FriendIdentity friend,
            string providerGameKey,
            int appId,
            string gameName,
            CancellationToken cancel)
        {
            var friendId = friend?.ExternalUserId?.Trim();
            var gameId = ResolveGameId(providerGameKey, appId);
            if (string.IsNullOrWhiteSpace(friendId) || gameId <= 0)
            {
                return FriendsProviderResult<FriendGameAchievements>.Failed("RetroAchievements friend id or game id is missing.");
            }

            try
            {
                var achievements = await FetchMappedAchievementsAsync(
                    gameId,
                    friendId,
                    useUserProgress: true,
                    cancel).ConfigureAwait(false);
                var rows = RetroAchievementsAchievementMapper.ToFriendRows(achievements);

                return FriendsProviderResult<FriendGameAchievements>.FromData(new FriendGameAchievements
                {
                    Friend = friend,
                    AppId = gameId,
                    ProviderGameKey = providerGameKey,
                    LastUpdatedUtc = DateTime.UtcNow,
                    StatsUnavailable = rows.Count == 0,
                    Rows = rows
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RAFriends] Failed to fetch achievements for friend={friendId}, gameId={gameId}.");
                return FriendsProviderResult<FriendGameAchievements>.Failed(
                    "RetroAchievements friend achievements could not be fetched.",
                    transientFailure: true);
            }
        }

        public async Task<FriendsProviderResult<FriendGameDefinition>> GetFriendGameDefinitionAsync(
            string providerGameKey,
            int appId,
            string gameName,
            CancellationToken cancel)
        {
            var gameId = ResolveGameId(providerGameKey, appId);
            if (gameId <= 0)
            {
                return FriendsProviderResult<FriendGameDefinition>.Failed("RetroAchievements game id is missing.");
            }

            try
            {
                var gameInfo = await _apiResolver().GetGameExtendedAsync(gameId, cancel).ConfigureAwait(false);
                var achievements = await FetchMappedAchievementsAsync(
                    gameId,
                    userId: null,
                    useUserProgress: false,
                    preloadedGameInfo: gameInfo,
                    cancel: cancel).ConfigureAwait(false);

                return FriendsProviderResult<FriendGameDefinition>.FromData(new FriendGameDefinition
                {
                    ProviderKey = Provider,
                    AppId = gameId,
                    ProviderGameKey = providerGameKey,
                    ProviderPlatformKey = null,
                    GameName = FirstNonEmpty(gameName, gameInfo?.GameTitle, $"RetroAchievements Game {gameId.ToString(CultureInfo.InvariantCulture)}"),
                    IconUrl = RetroAchievementsAchievementMapper.NormalizeImageUrl(
                        FirstNonEmpty(gameInfo?.ImageBoxArt, gameInfo?.ImageTitle, gameInfo?.ImageIngame, gameInfo?.ImageIcon)),
                    Status = achievements.Count > 0 ? FriendGameDefinitionStatus.Ok : FriendGameDefinitionStatus.NoAchievements,
                    LastCheckedUtc = DateTime.UtcNow,
                    Achievements = achievements
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RAFriends] Failed to fetch game definition for gameId={gameId}.");
                return FriendsProviderResult<FriendGameDefinition>.Failed(
                    "RetroAchievements game definition could not be fetched.",
                    transientFailure: true);
            }
        }

        private async Task<List<RaFollowedUser>> FetchFollowedUsersAsync(
            RetroAchievementsApiClient api,
            CancellationToken cancel)
        {
            var result = new List<RaFollowedUser>();
            var offset = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var page = await api.GetUsersIFollowAsync(offset, PageSize, cancel).ConfigureAwait(false);
                var rows = page?.Results ?? new List<RaFollowedUser>();
                result.AddRange(rows);

                if (rows.Count == 0 || rows.Count < PageSize)
                {
                    break;
                }

                offset += rows.Count;
                if (page.Total > 0 && offset >= page.Total)
                {
                    break;
                }
            }

            return result;
        }

        private async Task<List<FriendGameOwnership>> BuildOwnedGameRowsAsync(
            string friendId,
            IReadOnlyList<RaUserCompletionProgressItem> progress,
            CancellationToken cancel)
        {
            var items = (progress ?? Array.Empty<RaUserCompletionProgressItem>())
                .Where(item => item != null && item.GameID > 0)
                .ToList();
            var baseItems = items
                .Where(item => !RetroAchievementsSubsetTitleResolver.IsSubsetLikeTitle(item.Title))
                .ToList();
            var baseItemsByTitle = BuildBaseItemsByTitle(baseItems);
            var rowsByAppId = new Dictionary<int, FriendGameOwnership>();
            var extendedInfoCache = new Dictionary<int, RaGameInfoUserProgress>();

            foreach (var item in baseItems)
            {
                MergeOwnershipRow(rowsByAppId, CreateOwnershipRow(friendId, item), preferMetadata: true);
            }

            foreach (var item in items.Where(item => RetroAchievementsSubsetTitleResolver.IsSubsetLikeTitle(item.Title)))
            {
                cancel.ThrowIfCancellationRequested();

                var mappings = await ResolveSubsetBaseMappingsAsync(
                    item,
                    baseItemsByTitle,
                    extendedInfoCache,
                    cancel).ConfigureAwait(false);
                if (mappings.Count == 0)
                {
                    MergeOwnershipRow(rowsByAppId, CreateOwnershipRow(friendId, item), preferMetadata: true);
                    continue;
                }

                foreach (var mapping in mappings)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (mapping == null || mapping.BaseGameId <= 0)
                    {
                        continue;
                    }

                    var baseInfo = await GetExtendedInfoCachedAsync(
                        extendedInfoCache,
                        mapping.BaseGameId,
                        cancel).ConfigureAwait(false);
                    var row = CreateOwnershipRow(
                        friendId,
                        item,
                        mapping.BaseGameId,
                        FirstNonEmpty(mapping.BaseGameTitle, baseInfo?.GameTitle, RetroAchievementsSubsetTitleResolver.ExtractBaseTitle(item.Title)),
                        baseInfo);
                    MergeOwnershipRow(rowsByAppId, row, preferMetadata: false);
                }
            }

            return rowsByAppId.Values.ToList();
        }

        private async Task<List<RaSubsetBaseMapping>> ResolveSubsetBaseMappingsAsync(
            RaUserCompletionProgressItem subsetItem,
            IReadOnlyDictionary<string, List<RaUserCompletionProgressItem>> baseItemsByTitle,
            Dictionary<int, RaGameInfoUserProgress> extendedInfoCache,
            CancellationToken cancel)
        {
            var existingBaseRows = ResolveExistingBaseRowsForSubset(subsetItem, baseItemsByTitle);
            if (existingBaseRows.Count > 0)
            {
                return existingBaseRows
                    .Select(item => new RaSubsetBaseMapping
                    {
                        BaseGameId = item.GameID,
                        BaseGameTitle = item.Title,
                        Subset = new RaSubsetEntry { Id = subsetItem.GameID, Title = subsetItem.Title }
                    })
                    .ToList();
            }

            List<RaSubsetBaseMapping> indexMappings = null;
            try
            {
                if (subsetItem.ConsoleID > 0)
                {
                    indexMappings = await _hashIndexResolver()
                        .GetBaseGamesForSubsetAsync(subsetItem.GameID, subsetItem.ConsoleID, cancel)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RAFriends] Failed to resolve base game for subset '{subsetItem.Title}' (ID={subsetItem.GameID}) from hash index.");
            }

            var subsetInfo = await GetExtendedInfoCachedAsync(
                extendedInfoCache,
                subsetItem.GameID,
                cancel).ConfigureAwait(false);
            if (subsetInfo?.ParentGameId > 0)
            {
                var parentGameId = subsetInfo.ParentGameId.Value;
                var parentMapping = indexMappings?
                    .FirstOrDefault(mapping => mapping != null && mapping.BaseGameId == parentGameId);
                return new List<RaSubsetBaseMapping>
                {
                    new RaSubsetBaseMapping
                    {
                        BaseGameId = parentGameId,
                        BaseGameTitle = parentMapping?.BaseGameTitle,
                        Subset = new RaSubsetEntry { Id = subsetItem.GameID, Title = subsetItem.Title }
                    }
                };
            }

            return indexMappings ?? new List<RaSubsetBaseMapping>();
        }

        private async Task<RaGameInfoUserProgress> GetExtendedInfoCachedAsync(
            Dictionary<int, RaGameInfoUserProgress> cache,
            int gameId,
            CancellationToken cancel)
        {
            if (gameId <= 0)
            {
                return null;
            }

            if (cache.TryGetValue(gameId, out var cached))
            {
                return cached;
            }

            try
            {
                var info = await _apiResolver().GetGameExtendedAsync(gameId, cancel).ConfigureAwait(false);
                cache[gameId] = info;
                return info;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[RAFriends] Failed to fetch extended metadata for gameId={gameId}.");
                cache[gameId] = null;
                return null;
            }
        }

        private static Dictionary<string, List<RaUserCompletionProgressItem>> BuildBaseItemsByTitle(
            IEnumerable<RaUserCompletionProgressItem> baseItems)
        {
            var result = new Dictionary<string, List<RaUserCompletionProgressItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in baseItems ?? Enumerable.Empty<RaUserCompletionProgressItem>())
            {
                var key = NormalizeTitleKey(item?.Title);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<RaUserCompletionProgressItem>();
                    result[key] = list;
                }

                list.Add(item);
            }

            return result;
        }

        private static List<RaUserCompletionProgressItem> ResolveExistingBaseRowsForSubset(
            RaUserCompletionProgressItem subsetItem,
            IReadOnlyDictionary<string, List<RaUserCompletionProgressItem>> baseItemsByTitle)
        {
            var result = new List<RaUserCompletionProgressItem>();
            var baseTitle = RetroAchievementsSubsetTitleResolver.ExtractBaseTitle(subsetItem?.Title);
            if (string.IsNullOrWhiteSpace(baseTitle) || baseItemsByTitle == null)
            {
                return result;
            }

            var candidates = new List<string> { baseTitle };
            candidates.AddRange(RetroAchievementsSubsetTitleResolver.ExtractAlternateBaseTitleCandidates(baseTitle));
            foreach (var candidate in candidates)
            {
                var key = NormalizeTitleKey(candidate);
                if (string.IsNullOrWhiteSpace(key) ||
                    !baseItemsByTitle.TryGetValue(key, out var matches))
                {
                    continue;
                }

                foreach (var match in matches)
                {
                    if (match != null && !result.Any(existing => existing.GameID == match.GameID))
                    {
                        result.Add(match);
                    }
                }
            }

            return result;
        }

        private static FriendGameOwnership CreateOwnershipRow(
            string friendId,
            RaUserCompletionProgressItem item,
            int? overrideAppId = null,
            string overrideTitle = null,
            RaGameInfoUserProgress overrideGameInfo = null)
        {
            var appId = overrideAppId.GetValueOrDefault(item.GameID);
            var title = FirstNonEmpty(
                overrideTitle,
                overrideGameInfo?.GameTitle,
                item.Title,
                $"RetroAchievements Game {appId.ToString(CultureInfo.InvariantCulture)}");
            return new FriendGameOwnership
            {
                ProviderKey = Provider,
                ExternalUserId = friendId,
                AppId = appId,
                ProviderGameKey = null,
                ProviderPlatformKey = null,
                GameName = title,
                IconUrl = RetroAchievementsAchievementMapper.NormalizeImageUrl(
                    FirstNonEmpty(overrideGameInfo?.ImageIcon, item.ImageIcon)),
                CoverUrl = RetroAchievementsAchievementMapper.NormalizeImageUrl(
                    FirstNonEmpty(
                        overrideGameInfo?.ImageBoxArt,
                        overrideGameInfo?.ImageTitle,
                        overrideGameInfo?.ImageIngame,
                        overrideGameInfo?.ImageIcon,
                        item.ImageBoxArt,
                        item.ImageTitle,
                        item.ImageIngame,
                        item.ImageIcon)),
                PlaytimeForeverMinutes = 0,
                LastPlayedUtc = ParseFirstTimestamp(item.MostRecentAwardedDate, item.HighestAwardDate),
                AchievementUnlocksHint = Math.Max(0, item.NumAwarded),
                AchievementTotalHint = Math.Max(0, item.MaxPossible)
            };
        }

        private static void MergeOwnershipRow(
            Dictionary<int, FriendGameOwnership> rowsByAppId,
            FriendGameOwnership row,
            bool preferMetadata)
        {
            if (rowsByAppId == null || row == null || row.AppId <= 0)
            {
                return;
            }

            if (!rowsByAppId.TryGetValue(row.AppId, out var existing))
            {
                rowsByAppId[row.AppId] = row;
                return;
            }

            if (row.LastPlayedUtc.HasValue &&
                (!existing.LastPlayedUtc.HasValue || row.LastPlayedUtc.Value > existing.LastPlayedUtc.Value))
            {
                existing.LastPlayedUtc = row.LastPlayedUtc;
            }

            if (preferMetadata || string.IsNullOrWhiteSpace(existing.GameName))
            {
                existing.GameName = FirstNonEmpty(row.GameName, existing.GameName);
            }

            if (preferMetadata || string.IsNullOrWhiteSpace(existing.IconUrl))
            {
                existing.IconUrl = FirstNonEmpty(row.IconUrl, existing.IconUrl);
            }

            if (preferMetadata || string.IsNullOrWhiteSpace(existing.CoverUrl))
            {
                existing.CoverUrl = FirstNonEmpty(row.CoverUrl, existing.CoverUrl);
            }

            if (row.AchievementUnlocksHint.HasValue &&
                (!existing.AchievementUnlocksHint.HasValue ||
                 row.AchievementUnlocksHint.Value > existing.AchievementUnlocksHint.Value))
            {
                existing.AchievementUnlocksHint = row.AchievementUnlocksHint;
            }

            if (row.AchievementTotalHint.HasValue &&
                (!existing.AchievementTotalHint.HasValue ||
                 row.AchievementTotalHint.Value > existing.AchievementTotalHint.Value))
            {
                existing.AchievementTotalHint = row.AchievementTotalHint;
            }
        }

        private async Task<List<RaUserCompletionProgressItem>> FetchCompletionProgressAsync(
            RetroAchievementsApiClient api,
            string friendId,
            CancellationToken cancel)
        {
            var result = new List<RaUserCompletionProgressItem>();
            var offset = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var page = await api.GetUserCompletionProgressAsync(friendId, offset, PageSize, cancel).ConfigureAwait(false);
                var rows = page?.Results ?? new List<RaUserCompletionProgressItem>();
                result.AddRange(rows);

                if (rows.Count == 0 || rows.Count < PageSize)
                {
                    break;
                }

                offset += rows.Count;
                if (page.Total > 0 && offset >= page.Total)
                {
                    break;
                }
            }

            return result;
        }

        private async Task<List<AchievementDetail>> FetchMappedAchievementsAsync(
            int gameId,
            string userId,
            bool useUserProgress,
            CancellationToken cancel,
            RaGameInfoUserProgress preloadedGameInfo = null)
        {
            var raSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            var api = _apiResolver();
            var gameInfo = preloadedGameInfo ??
                           (useUserProgress
                               ? await api.GetGameInfoAndUserProgressAsync(gameId, userId, cancel).ConfigureAwait(false)
                               : await api.GetGameExtendedAsync(gameId, cancel).ConfigureAwait(false));

            var subsetConsoleId = RetroAchievementsSubsetConsoleResolver.Resolve(gameInfo, null);

            var sets = await RetroAchievementsSetAssembler.AssembleAsync(
                gameInfo,
                gameId,
                subsetConsoleId,
                _hashIndexResolver(),
                raSettings,
                (setId, ct) => useUserProgress
                    ? api.GetGameInfoAndUserProgressAsync(setId, userId, ct)
                    : api.GetGameExtendedAsync(setId, ct),
                _logger,
                cancel,
                logPrefix: "[RAFriends]").ConfigureAwait(false);

            return sets.Achievements;
        }

        private static int ResolveGameId(string providerGameKey, int appId)
        {
            if (appId > 0)
            {
                return appId;
            }

            return int.TryParse(
                (providerGameKey ?? string.Empty).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) && parsed > 0
                ? parsed
                : 0;
        }

        private static DateTime? ParseFirstTimestamp(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                var parsed = RetroAchievementsAchievementMapper.ParseRaUtcTimestamp(value);
                if (parsed.HasValue)
                {
                    return parsed;
                }
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static string NormalizeTitleKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return string.Join(
                " ",
                value.Trim()
                    .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();
        }
    }
}
