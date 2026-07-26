using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.ProgressReporting;
using PlayniteAchievements.Services.Friends;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;

namespace PlayniteAchievements.Services.Refresh
{
    /// <summary>
    /// Pure friend-refresh policy predicates: static helpers that decide which friend refresh
    /// work applies, computed only from their parameters. Extracted from RefreshRuntime.
    /// </summary>
    internal static class FriendRefreshWorkPolicy
    {
        internal static bool ShouldTrySteamGameImageFallback(string providerKey, FriendGameOwnership source)
        {
            return source?.AppId > 0 &&
                   string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDistinctCacheableImageSource(string candidate, string original)
        {
            return DiskImageService.IsCacheableImageSource(candidate) &&
                   !string.Equals(candidate?.Trim(), original?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasPositiveUnlockHint(FriendGameOwnership ownership)
        {
            return ownership?.AchievementUnlocksHint > 0;
        }

        internal static bool ShouldSeedDefinitionsFromFriendAchievementScrape(string providerKey)
        {
            // Exophase friend unlocks come from the earned-awards JSON endpoint, whose rows carry only
            // the stable award id (no names/descriptions), so definitions cannot be seeded from the
            // unlock rows. They come from the once-per-game schema fetch (GetFriendGameDefinitionAsync),
            // shared across all friends.
            return false;
        }

        internal static bool HasZeroUnlockHint(FriendGameOwnership ownership)
        {
            return ownership?.AchievementUnlocksHint.HasValue == true &&
                   ownership.AchievementUnlocksHint.GetValueOrDefault() <= 0;
        }

        internal static bool IsExplicitProviderGameTarget(
            FriendRefreshOptions options,
            int appId,
            string providerGameKey)
        {
            if (options == null)
            {
                return false;
            }

            if (appId > 0 && options.ProviderAppIds?.Any(id => id == appId) == true)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(providerGameKey) &&
                options.ProviderGameKeys?.Any(key =>
                    string.Equals(key?.Trim(), providerGameKey.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
            {
                return true;
            }

            return false;
        }

        internal static bool HasExplicitProviderGameTargets(FriendRefreshOptions options)
        {
            return options?.ProviderAppIds?.Any(id => id > 0) == true ||
                   options?.ProviderGameKeys?.Any(key => !string.IsNullOrWhiteSpace(key)) == true;
        }

        internal static bool ShouldPruneStaleSharedOwnership(
            FriendRefreshOptions options,
            IReadOnlyList<FriendGameOwnership> ownedGames)
        {
            return !HasExplicitProviderGameTargets(options) &&
                   ownedGames != null &&
                   ownedGames.Count > 0;
        }

        internal static bool IsFocusedFriendGameRefresh(FriendRefreshOptions options)
        {
            return options?.Scope == FriendRefreshScope.SelectedGame ||
                   HasExplicitProviderGameTargets(options) ||
                   (options?.Scope == FriendRefreshScope.Custom &&
                    options.PlayniteGameIds?.Any(id => id != Guid.Empty) == true);
        }

        internal static bool IsTransientError(Exception ex)
        {
            if (ex == null || ex is OperationCanceledException)
            {
                return false;
            }

            var message = ex.Message ?? string.Empty;
            if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("temporarily", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("502", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("503", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("504", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (message.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 message.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return ex.InnerException != null &&
                   !ReferenceEquals(ex.InnerException, ex) &&
                   IsTransientError(ex.InnerException);
        }

        internal static bool IsDefinitionCheckDue(FriendGameDefinitionState state)
        {
            // No time-based expiry: a cached complete (Ok) schema is reused indefinitely; only
            // never-checked or non-Ok states are re-fetched. A forced refresh (Full/selected-game)
            // overrides this and re-fetches every discovered game.
            return state == null ||
                   !state.LastCheckedUtc.HasValue ||
                   state.Status != FriendGameDefinitionStatus.Ok;
        }

        internal static bool HasProviderGameIdentity(FriendGameOwnership ownership)
        {
            return ownership != null && HasProviderGameIdentity(ownership.AppId, ownership.ProviderGameKey);
        }

        internal static bool HasProviderGameIdentity(int appId, string providerGameKey)
        {
            return appId > 0 || !string.IsNullOrWhiteSpace(providerGameKey);
        }

        // Recent-scope recency: does the freshly-fetched ownership show new activity since the last
        // successful scrape? Steam uses playtime (the reliably-scraped signal); RA/Exophase use the
        // last-played / last-unlock timestamp. Never-seen, never-scraped, and previously-failed games
        // are always considered stale so they get (re)scraped.
        internal static bool IsRecencyStale(string providerKey, FriendGameOwnership fresh, FriendOwnershipRecency prev)
        {
            if (fresh == null)
            {
                return false;
            }

            if (prev == null || !prev.LastScrapedUtc.HasValue ||
                !string.Equals(prev.LastScrapeStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                return fresh.PlaytimeForeverMinutes > prev.PlaytimeForeverMinutes;
            }

            return fresh.LastPlayedUtc.HasValue &&
                   (!prev.LastPlayedUtc.HasValue || fresh.LastPlayedUtc.Value > prev.LastPlayedUtc.Value);
        }

        internal static bool ShouldRefreshOwnership(string providerKey, FriendRefreshOptions options)
        {
            if (options == null)
            {
                return false;
            }

            // Full, Shared, Installed and Recent all resolve their candidates from the friend's cached
            // ownership, so each needs the ownership fetched. SelectedGame targets a specific
            // current-user library game (cross-joined with friends) and needs no ownership fetch.
            if (options.Scope == FriendRefreshScope.Full ||
                options.Scope == FriendRefreshScope.Shared ||
                options.Scope == FriendRefreshScope.Installed ||
                options.Scope == FriendRefreshScope.Recent)
            {
                return true;
            }

            if (HasExplicitProviderGameTargets(options))
            {
                return true;
            }

            if (options.Scope == FriendRefreshScope.SelectedGame ||
                options.PlayniteGameIds?.Any(id => id != Guid.Empty) == true)
            {
                return false;
            }

            return options.Scope == FriendRefreshScope.Custom && RequiresOwnershipMapping(providerKey);
        }

        internal static bool RequiresOwnershipMapping(string providerKey)
        {
            return string.Equals(providerKey, "Exophase", StringComparison.OrdinalIgnoreCase);
        }

        // Providers whose friend ownership items carry no inline PlayniteGameId and match the
        // current-user library by plain (AppId/ProviderGameKey) identity. Their items get the id
        // stamped from current-user labels before the shared ownership save, so shared games gain a
        // library-mapped Games row (and a FriendOwnership row) even when the friend has no unlocks.
        // Exophase is excluded: it resolves inline ids itself with platform-aware name matching, and
        // its string keys must not fall back to naive key equality (see
        // SqlNadoCacheStore.ShouldUseSharedFriendGameFallback).
        internal static bool ShouldMapOwnershipFromCurrentUserLabels(string providerKey)
        {
            return string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase);
        }

        // The discovery scopes (Full/Shared/Installed) resolve their scrape candidates from the fresh,
        // hint-bearing ownership snapshot (game-centric). Recent draws from the whole cached friend
        // library filtered by the recency gate, and SelectedGame/Custom target specific games across
        // friends; those keep the cache-backed candidate loader.
        internal static bool UsesSnapshotCandidateBuilder(FriendRefreshOptions options)
        {
            switch (options?.Scope)
            {
                case FriendRefreshScope.Full:
                case FriendRefreshScope.Shared:
                case FriendRefreshScope.Installed:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ShouldDiscoverUnowned(string providerKey, FriendRefreshOptions options)
        {
            return options?.DiscoversProviderOnlyGames() == true &&
                   SupportsProviderOnlyFriendDetails(providerKey);
        }

        // Providers whose provider-only friend games get their icon/cover from the achievements-page
        // header banner (downloaded during the definition fetch). The generic profile-thumbnail
        // download must be skipped for them: SaveProviderGameImagePaths lets a non-null value win via
        // COALESCE, so a small thumbnail would overwrite the higher-quality banner.
        internal static bool PrefersDefinitionHeaderBannerImages(string providerKey)
        {
            return string.Equals(providerKey, "Exophase", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldGuardProviderOnlyZeroUnlocks(string providerKey)
        {
            return SupportsProviderOnlyFriendDetails(providerKey);
        }

        internal static bool SupportsProviderOnlyFriendDetails(string providerKey)
        {
            return string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerKey, "Exophase", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasAnyUnlockedFriendAchievements(FriendGameAchievements achievements)
        {
            return achievements?.Rows?.Any(row => row?.Unlocked == true) == true;
        }
    }
}
