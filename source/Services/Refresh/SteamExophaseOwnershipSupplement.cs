using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services.Friends;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Refresh
{
    internal sealed class SteamExophaseFriendOwnershipMapResult
    {
        public List<FriendGameOwnership> Ownership { get; set; } = new List<FriendGameOwnership>();
        public int IncomingCount { get; set; }
        public int SkippedCount { get; set; }
    }

    /// <summary>
    /// Supplements a Steam friend's ownership with Steam-family games from the merged Exophase
    /// account. Applies only when UseExophaseForSteamFriendOwnership is on, the Exophase provider is
    /// enabled, and the Steam friend is merged with a non-ignored Exophase friend. Exophase rows are
    /// translated to Steam rows (AppId, Steam image URLs) and only add AppIds the Steam list lacks; any
    /// failure keeps the Steam ownership unchanged.
    /// </summary>
    internal sealed class SteamExophaseOwnershipSupplement
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievements.Providers.ProviderRegistry _providerRegistry;
        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly IFriendCacheManager _friendCache;
        private readonly Func<IReadOnlyList<CurrentUserGameLabel>> _loadCurrentUserLabels;
        private readonly ILogger _logger;

        public SteamExophaseOwnershipSupplement(
            PlayniteAchievementsSettings settings,
            PlayniteAchievements.Providers.ProviderRegistry providerRegistry,
            IReadOnlyList<IDataProvider> providers,
            IFriendCacheManager friendCache,
            Func<IReadOnlyList<CurrentUserGameLabel>> loadCurrentUserLabels,
            ILogger logger)
        {
            _settings = settings;
            _providerRegistry = providerRegistry;
            _providers = providers;
            _friendCache = friendCache;
            _loadCurrentUserLabels = loadCurrentUserLabels;
            _logger = logger;
        }

        // Returns the ownership result with Exophase-sourced Steam rows added, or the given result
        // unchanged when the supplement does not apply, fails, or adds nothing. The supplemented
        // result keeps the Steam result's IsPartial: Exophase only adds games, so completeness is
        // still the Steam list's.
        public async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> SupplementAsync(
            string providerKey,
            FriendIdentity steamFriend,
            FriendsProviderResult<IReadOnlyList<FriendGameOwnership>> steamOwnershipResult,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
            RateLimiter limiter,
            CancellationToken cancel)
        {
            var exophaseUserId = ResolveMergedExophaseFriend(providerKey, steamFriend?.ExternalUserId);
            if (steamOwnershipResult?.Success != true)
            {
                return steamOwnershipResult;
            }

            var supplemented = await TryGetSupplementedOwnershipAsync(
                providerKey,
                steamFriend,
                exophaseUserId,
                steamOwnershipResult.Data,
                currentUserLabels,
                limiter,
                cancel).ConfigureAwait(false);
            if (supplemented != null)
            {
                supplemented.IsPartial = steamOwnershipResult.IsPartial;
            }

            return supplemented ?? steamOwnershipResult;
        }

        private async Task<FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>> TryGetSupplementedOwnershipAsync(
            string providerKey,
            FriendIdentity steamFriend,
            string exophaseUserId,
            IReadOnlyList<FriendGameOwnership> knownSteamOwnership,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels,
            RateLimiter limiter,
            CancellationToken cancel)
        {
            if (!string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(steamFriend?.ExternalUserId) ||
                string.IsNullOrWhiteSpace(exophaseUserId))
            {
                return null;
            }

            var source = ResolveExophaseSource();
            if (source == null)
            {
                return null;
            }

            try
            {
                // Reuse the labels resolved once during provider preparation instead of
                // re-materializing the whole current-user library (and the Playnite games list)
                // for every friend; fall back to a fresh load only if they were not supplied.
                var resolvedLabels = currentUserLabels ?? _loadCurrentUserLabels?.Invoke();
                var exophaseResult = await limiter.ExecuteWithRetryAsync(
                    () => source.GetSteamOwnedGamesAsync(exophaseUserId, resolvedLabels, knownSteamOwnership, cancel),
                    FriendRefreshWorkPolicy.IsTransientError,
                    cancel).ConfigureAwait(false);
                if (exophaseResult?.Success != true)
                {
                    _logger?.Debug(
                        $"Exophase Steam ownership augmentation unavailable for Steam friend {steamFriend.ExternalUserId} " +
                        $"via Exophase/{exophaseUserId}: {exophaseResult?.ErrorMessage ?? "provider unavailable"}. Keeping Steam ownership.");
                    return null;
                }

                var steamMappings = _friendCache?.LoadFriendGameMappings("Steam") ??
                                    new List<FriendGameMapping>();
                var mapped = MapToSteamOwnership(
                    steamFriend.ExternalUserId,
                    exophaseResult.Data,
                    steamMappings,
                    currentUserLabels);
                if (mapped.Ownership.Count == 0)
                {
                    _logger?.Debug(
                        $"Exophase Steam ownership augmentation produced no extra Steam-mapped rows for Steam friend {steamFriend.ExternalUserId} " +
                        $"via Exophase/{exophaseUserId} (incoming={mapped.IncomingCount}, skipped={mapped.SkippedCount}). Keeping Steam ownership.");
                    return null;
                }

                var combined = MergeSteamOwnershipWithSupplement(knownSteamOwnership, mapped.Ownership);
                var baseCount = knownSteamOwnership?.Count ?? 0;
                var addedCount = combined.Count - baseCount;
                if (addedCount <= 0)
                {
                    _logger?.Debug(
                        $"Exophase Steam ownership augmentation found no new Steam AppIDs for Steam friend {steamFriend.ExternalUserId} " +
                        $"via Exophase/{exophaseUserId} (incoming={mapped.IncomingCount}, mapped={mapped.Ownership.Count}, skipped={mapped.SkippedCount}).");
                    return null;
                }

                _logger?.Info(
                    $"Augmented Steam friend ownership with Exophase for Steam friend {steamFriend.ExternalUserId} " +
                    $"via Exophase/{exophaseUserId}: steamBase={baseCount}, incoming={mapped.IncomingCount}, " +
                    $"mapped={mapped.Ownership.Count}, added={addedCount}, skipped={mapped.SkippedCount}.");
                return FriendsProviderResult<IReadOnlyList<FriendGameOwnership>>.FromData(combined);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(
                    ex,
                    $"Exophase Steam ownership augmentation failed for Steam friend {steamFriend.ExternalUserId} " +
                    $"via Exophase/{exophaseUserId}. Keeping Steam ownership.");
                return null;
            }
        }

        // Safety net over the Exophase-provided rows: the Steam list wins, and a supplemental row is
        // added only when its AppId is not already known.
        private static IReadOnlyList<FriendGameOwnership> MergeSteamOwnershipWithSupplement(
            IReadOnlyList<FriendGameOwnership> steamOwnership,
            IReadOnlyList<FriendGameOwnership> supplementalOwnership)
        {
            var merged = new List<FriendGameOwnership>();
            var seenAppIds = new HashSet<int>();

            foreach (var item in steamOwnership ?? Array.Empty<FriendGameOwnership>())
            {
                if (item == null)
                {
                    continue;
                }

                if (item.AppId > 0)
                {
                    seenAppIds.Add(item.AppId);
                }

                merged.Add(item);
            }

            foreach (var item in supplementalOwnership ?? Array.Empty<FriendGameOwnership>())
            {
                if (item == null)
                {
                    continue;
                }

                if (item.AppId > 0 && !seenAppIds.Add(item.AppId))
                {
                    continue;
                }

                merged.Add(item);
            }

            return merged;
        }

        private string ResolveMergedExophaseFriend(string providerKey, string steamExternalUserId)
        {
            if (!string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(steamExternalUserId) ||
                !IsEnabled())
            {
                return null;
            }

            var persisted = _settings?.Persisted;
            var group = persisted?.GetFriendMergeGroupForAccount("Steam", steamExternalUserId);
            var exophaseMember = group?.Members?
                .FirstOrDefault(member => string.Equals(member?.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(exophaseMember?.ExternalUserId))
            {
                return null;
            }

            var exophaseFriend = persisted.GetFriendSetting("Exophase", exophaseMember.ExternalUserId);
            return exophaseFriend == null ||
                   exophaseFriend.IsIgnored ||
                   string.IsNullOrWhiteSpace(exophaseFriend.ExternalUserId)
                ? null
                : exophaseFriend.ExternalUserId.Trim();
        }

        private bool IsEnabled()
        {
            return _settings?.Persisted?.UseExophaseForSteamFriendOwnership == true &&
                   IsExophaseProviderEnabled() &&
                   ResolveExophaseSource() != null;
        }

        private bool IsExophaseProviderEnabled()
        {
            const string providerKey = "Exophase";
            if (_providerRegistry != null)
            {
                return _providerRegistry.IsProviderEnabled(providerKey);
            }

            try
            {
                var provider = _providers?
                    .FirstOrDefault(item => string.Equals(item?.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
                var settings = provider?.GetSettings();
                return settings == null || settings.IsEnabled;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to resolve provider enabled state for {providerKey}.");
                return false;
            }
        }

        private ISteamFriendOwnershipSupplementSource ResolveExophaseSource()
        {
            return _providers?
                .FirstOrDefault(provider => string.Equals(provider?.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase))
                ?.Friends as ISteamFriendOwnershipSupplementSource;
        }

        // Translates Exophase friend ownership into Steam rows. The AppId comes from the cached Steam
        // game mapping or the current user's Steam-family library label for the row's Playnite game,
        // falling back to the row's own AppId; rows without an AppId or repeating one are skipped.
        internal static SteamExophaseFriendOwnershipMapResult MapToSteamOwnership(
            string steamExternalUserId,
            IReadOnlyList<FriendGameOwnership> exophaseOwnership,
            IReadOnlyList<FriendGameMapping> steamMappings,
            IReadOnlyList<CurrentUserGameLabel> currentUserLabels = null)
        {
            var result = new SteamExophaseFriendOwnershipMapResult
            {
                IncomingCount = exophaseOwnership?.Count ?? 0
            };

            if (string.IsNullOrWhiteSpace(steamExternalUserId) ||
                exophaseOwnership == null ||
                exophaseOwnership.Count == 0)
            {
                result.SkippedCount = result.IncomingCount;
                return result;
            }

            var steamMappingByPlayniteId = (steamMappings ?? Array.Empty<FriendGameMapping>())
                .Where(mapping => mapping != null &&
                                  mapping.AppId > 0 &&
                                  mapping.PlayniteGameId != Guid.Empty)
                .GroupBy(mapping => mapping.PlayniteGameId)
                .ToDictionary(group => group.Key, group => group.First());
            var steamMappingByAppId = (steamMappings ?? Array.Empty<FriendGameMapping>())
                .Where(mapping => mapping != null && mapping.AppId > 0)
                .GroupBy(mapping => mapping.AppId)
                .ToDictionary(group => group.Key, group => group.First());

            var steamLabelByPlayniteId = (currentUserLabels ?? Array.Empty<CurrentUserGameLabel>())
                .Where(label => label != null &&
                                label.AppId > 0 &&
                                label.PlayniteGameId != Guid.Empty &&
                                string.Equals(
                                    ExophaseFriendPlatformMatcher.ResolveStoredGameFamilyKey(
                                        label.ProviderKey,
                                        label.ProviderPlatformKey),
                                    "Steam",
                                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(label => label.PlayniteGameId)
                .ToDictionary(group => group.Key, group => group.First());
            var steamLabelByAppId = (currentUserLabels ?? Array.Empty<CurrentUserGameLabel>())
                .Where(label => label != null &&
                                label.AppId > 0 &&
                                label.PlayniteGameId != Guid.Empty &&
                                string.Equals(
                                    ExophaseFriendPlatformMatcher.ResolveStoredGameFamilyKey(
                                        label.ProviderKey,
                                        label.ProviderPlatformKey),
                                    "Steam",
                                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(label => label.AppId)
                .ToDictionary(group => group.Key, group => group.First());

            var seenAppIds = new HashSet<int>();
            foreach (var item in exophaseOwnership)
            {
                if (item == null)
                {
                    result.SkippedCount++;
                    continue;
                }

                Guid? playniteGameId = null;
                FriendGameMapping mapping = null;
                CurrentUserGameLabel label = null;
                if (item.PlayniteGameId.HasValue && item.PlayniteGameId.Value != Guid.Empty)
                {
                    playniteGameId = item.PlayniteGameId.Value;
                    steamMappingByPlayniteId.TryGetValue(playniteGameId.Value, out mapping);
                    steamLabelByPlayniteId.TryGetValue(playniteGameId.Value, out label);
                }

                var appId = mapping?.AppId > 0
                    ? mapping.AppId
                    : (label?.AppId > 0
                        ? label.AppId
                        : Math.Max(0, item.AppId));
                if (appId > 0)
                {
                    if (mapping == null)
                    {
                        steamMappingByAppId.TryGetValue(appId, out mapping);
                    }

                    if (label == null)
                    {
                        steamLabelByAppId.TryGetValue(appId, out label);
                    }

                    if (!playniteGameId.HasValue || playniteGameId.Value == Guid.Empty)
                    {
                        if (mapping != null && mapping.PlayniteGameId != Guid.Empty)
                        {
                            playniteGameId = mapping.PlayniteGameId;
                        }
                        else if (label != null && label.PlayniteGameId != Guid.Empty)
                        {
                            playniteGameId = label.PlayniteGameId;
                        }
                    }
                }

                if (appId <= 0 || !seenAppIds.Add(appId))
                {
                    result.SkippedCount++;
                    continue;
                }

                result.Ownership.Add(new FriendGameOwnership
                {
                    ProviderKey = "Steam",
                    ExternalUserId = steamExternalUserId.Trim(),
                    AppId = appId,
                    ProviderGameKey = !string.IsNullOrWhiteSpace(mapping?.ProviderGameKey)
                        ? mapping.ProviderGameKey
                        : label?.ProviderGameKey,
                    ProviderPlatformKey = "Steam",
                    PlayniteGameId = playniteGameId,
                    GameName = item.GameName,
                    IconUrl = SteamImageUrls.Icon(appId),
                    CoverUrl = SteamImageUrls.Cover(appId),
                    PlaytimeForeverMinutes = Math.Max(0, item.PlaytimeForeverMinutes),
                    Playtime2WeeksMinutes = item.Playtime2WeeksMinutes.HasValue
                        ? Math.Max(0, item.Playtime2WeeksMinutes.Value)
                        : (int?)null,
                    LastPlayedUtc = item.LastPlayedUtc,
                    AchievementUnlocksHint = item.AchievementUnlocksHint,
                    AchievementTotalHint = item.AchievementTotalHint
                });
            }

            return result;
        }
    }
}
