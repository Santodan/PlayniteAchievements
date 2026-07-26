using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.Friends;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamExophaseFriendOwnershipMapResult
    {
        public List<FriendGameOwnership> Ownership { get; set; } = new List<FriendGameOwnership>();
        public int IncomingCount { get; set; }
        public int SkippedCount { get; set; }
    }

    internal static class SteamExophaseFriendOwnershipMapper
    {
        public static SteamExophaseFriendOwnershipMapResult MapToSteamOwnership(
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
