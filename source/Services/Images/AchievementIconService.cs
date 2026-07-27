using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.ProgressReporting;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images
{
    public sealed class FriendGameImageCacheResult
    {
        public string IconPath { get; set; }
        public string CoverPath { get; set; }

        public bool HasAnyPath =>
            !string.IsNullOrWhiteSpace(IconPath) ||
            !string.IsNullOrWhiteSpace(CoverPath);
    }

    /// <summary>
    /// Handles icon path resolution and local icon cache population for achievements.
    /// </summary>
    public class AchievementIconService
    {
        private sealed class AchievementIconRequest
        {
            public AchievementDetail Achievement { get; set; }
            public AchievementIconVariant Variant { get; set; }
            public string SourcePath { get; set; }
            public string TargetPath { get; set; }
        }

        private readonly DiskImageService _diskImageService;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly PersistedSettings _settings;
        private readonly ILogger _logger;

        public AchievementIconService(
            DiskImageService diskImageService,
            ManagedCustomIconService managedCustomIconService,
            PersistedSettings settings,
            ILogger logger)
        {
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
            _managedCustomIconService = managedCustomIconService ?? throw new ArgumentNullException(nameof(managedCustomIconService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
        }

        public async Task PopulateAchievementIconCacheAsync(
            GameAchievementData data,
            CancellationToken cancel,
            Action<int, int> onIconProgress = null,
            bool forceRefreshExistingTargets = false)
        {
            await PopulateAchievementIconCacheAsync(
                    data,
                    null,
                    null,
                    cancel,
                    onIconProgress,
                    forceRefreshExistingTargets)
                .ConfigureAwait(false);
        }

        public async Task PopulateAchievementIconCacheAsync(
            GameAchievementData data,
            IReadOnlyDictionary<string, string> unlockedOverrides,
            IReadOnlyDictionary<string, string> lockedOverrides,
            CancellationToken cancel,
            Action<int, int> onIconProgress = null,
            bool forceRefreshExistingTargets = false,
            ISet<string> forceOverrideApiNames = null)
        {
            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var useSeparateLockedIcons = GameCustomDataLookup.ShouldUseSeparateLockedIcons(data?.PlayniteGameId, _settings);
            var gameId = ResolveGameId(data);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                data.Achievements.Select(achievement => achievement?.ApiName));

            // Normalize the effective icon sources once here so request building only downloads
            // the paths that can actually be used for display and persistence.
            await PrepareSourcePathsAsync(
                    data.Achievements,
                    fileStems,
                    gameId,
                    useSeparateLockedIcons,
                    unlockedOverrides,
                    lockedOverrides,
                    forceRefreshExistingTargets,
                    forceOverrideApiNames,
                    cancel)
                .ConfigureAwait(false);

            await PopulateAchievementIconsCoreAsync(
                    data.Achievements,
                    fileStems,
                    gameId,
                    buildLockedRequests: true,
                    forceRefreshExistingTargets: forceRefreshExistingTargets,
                    reportInitialProgress: false,
                    resolveTargetPath: (achievement, fileStem, variant) =>
                        _diskImageService.GetAchievementIconCachePath(
                            gameId,
                            fileStem,
                            variant),
                    resolveUnlockedFallback: achievement => achievement.UnlockedIconPath,
                    resolveLockedPath: (achievement, finalUnlockedPath, resolvedLockedCandidate) =>
                        ResolveOwnedLockedPath(
                            achievement,
                            finalUnlockedPath,
                            resolvedLockedCandidate,
                            gameId,
                            useSeparateLockedIcons),
                    cancel: cancel,
                    onIconProgress: onIconProgress)
                .ConfigureAwait(false);
        }

        public async Task PopulateFriendAvatarIconCacheAsync(
            string providerKey,
            FriendIdentity friend,
            string previousAvatarUrl,
            CancellationToken cancel)
        {
            if (friend == null || string.IsNullOrWhiteSpace(friend.AvatarUrl))
            {
                return;
            }

            var relativePath = FriendImageCachePathBuilder.BuildAvatarRelativePath(
                providerKey,
                friend.ExternalUserId);
            var changed = !string.Equals(
                previousAvatarUrl,
                friend.AvatarUrl,
                StringComparison.OrdinalIgnoreCase);
            var path = await ResolveCacheRelativeImagePathAsync(
                    friend.AvatarUrl,
                    relativePath,
                    cancel: cancel,
                    forceRefreshExistingTarget: changed)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                friend.AvatarPath = path;
            }
        }

        public async Task PopulateFriendAchievementIconCacheAsync(
            FriendGameDefinition definition,
            CancellationToken cancel,
            Action<int, int> onIconProgress = null)
        {
            if (definition?.Achievements == null || definition.Achievements.Count == 0)
            {
                return;
            }

            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                definition.Achievements.Select(achievement => achievement?.ApiName));

            // Friend achievement icons run through the same pipeline as owned achievements; only the
            // output location differs (friendgames/{provider}/{gameKey}), and friends never download
            // a separate locked icon (the locked look is derived from the unlocked one) or apply the
            // owned-only custom-icon overrides.
            await PopulateAchievementIconsCoreAsync(
                    definition.Achievements,
                    fileStems,
                    gameId: null,
                    buildLockedRequests: false,
                    forceRefreshExistingTargets: false,
                    reportInitialProgress: true,
                    resolveTargetPath: (achievement, fileStem, variant) =>
                        _diskImageService.ResolveCacheRelativePath(
                            FriendImageCachePathBuilder.BuildGameImageRelativePath(
                                definition.ProviderKey,
                                definition.ProviderGameKey,
                                FriendImageCachePathBuilder.GetAchievementFileName(fileStem, variant))),
                    resolveUnlockedFallback: ResolveUnlockedSourcePath,
                    resolveLockedPath: (achievement, finalUnlockedPath, resolvedLockedCandidate) => finalUnlockedPath,
                    cancel: cancel,
                    onIconProgress: onIconProgress)
                .ConfigureAwait(false);
        }

        public async Task<string> PopulateFriendGameIconCacheAsync(
            string providerKey,
            string providerGameKey,
            string sourcePath,
            CancellationToken cancel,
            Action onImageResolved = null)
        {
            return await PopulateFriendGameImageFileCacheAsync(
                    providerKey,
                    providerGameKey,
                    sourcePath,
                    FriendImageCachePathBuilder.GameIconFileName,
                    cancel,
                    onImageResolved)
                .ConfigureAwait(false);
        }

        public async Task<FriendGameImageCacheResult> PopulateFriendGameImageCacheAsync(
            string providerKey,
            string providerGameKey,
            string iconSourcePath,
            string coverSourcePath,
            CancellationToken cancel,
            Action onImageResolved = null)
        {
            var requests = new List<AchievementIconRequest>();
            var iconRequest = AddStableImageRequest(
                requests,
                iconSourcePath,
                FriendImageCachePathBuilder.BuildGameImageRelativePath(
                    providerKey,
                    providerGameKey,
                    FriendImageCachePathBuilder.GameIconFileName));
            var coverRequest = AddStableImageRequest(
                requests,
                coverSourcePath,
                FriendImageCachePathBuilder.BuildGameImageRelativePath(
                    providerKey,
                    providerGameKey,
                    FriendImageCachePathBuilder.GameCoverFileName));

            var resolvedPaths = await ResolveIconRequestsAsync(
                    requests,
                    forceRefreshExistingTargets: false,
                    cancel: cancel,
                    onGroupCompleted: onImageResolved)
                .ConfigureAwait(false);

            return new FriendGameImageCacheResult
            {
                IconPath = TryGetResolvedPath(iconRequest, resolvedPaths),
                CoverPath = TryGetResolvedPath(coverRequest, resolvedPaths)
            };
        }

        // Removes a single friend game's cached images (achievement icons + game icon/cover) at
        // icon_cache/friendgames/{provider}/{gameKey}. Called when a provider-only friend game is
        // promoted to a Playnite-backed owned game and its provider-only icons become orphaned.
        public void DeleteFriendGameIconCache(string providerKey, string providerGameKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(providerGameKey))
            {
                return;
            }

            var relativeFolder = FriendImageCachePathBuilder.BuildGameFolderRelativePath(
                providerKey,
                providerGameKey);
            _diskImageService.DeleteCacheRelativeDirectory(relativeFolder);
        }

        // Removes a game's retired compressed 128px icon folder. Called after a refresh has
        // repopulated and persisted original-resolution icon paths for the game.
        public void DeleteLegacyCompressedIconFolder(string gameId)
        {
            _diskImageService.DeleteLegacyCompressedGameIconFolder(gameId);
        }

        private async Task<string> ResolveCacheRelativeImagePathAsync(
            string sourcePath,
            string relativeTargetPath,
            CancellationToken cancel,
            bool forceRefreshExistingTarget = false)
        {
            var targetPath = _diskImageService.ResolveCacheRelativePath(relativeTargetPath);
            return await ResolvePrimaryPathAsync(
                    sourcePath,
                    targetPath,
                    forceRefreshExistingTarget,
                    cancel)
                .ConfigureAwait(false);
        }

        private async Task PrepareSourcePathsAsync(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> fileStems,
            string gameId,
            bool useSeparateLockedIcons,
            IReadOnlyDictionary<string, string> unlockedOverrides,
            IReadOnlyDictionary<string, string> lockedOverrides,
            bool overwriteExistingTargets,
            ISet<string> forceOverrideApiNames,
            CancellationToken cancel)
        {
            if (achievements == null || achievements.Count == 0)
            {
                return;
            }

            var hasOverrides = AchievementIconOverrideHelper.HasOverrides(unlockedOverrides, lockedOverrides);

            for (var i = 0; i < achievements.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var achievement = achievements[i];
                var apiName = NormalizeText(achievement?.ApiName);
                if (achievement == null)
                {
                    continue;
                }

                var fileStem = default(string);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    fileStems.TryGetValue(apiName, out fileStem);
                }

                var forceThisAchievement = !string.IsNullOrWhiteSpace(apiName) &&
                    forceOverrideApiNames?.Contains(apiName) == true;
                var overwriteExistingTarget = overwriteExistingTargets || forceThisAchievement;

                var resolvedUnlockedOverride = default(string);
                var resolvedLockedOverride = default(string);

                if (hasOverrides && !string.IsNullOrWhiteSpace(fileStem))
                {
                    var unlockedSource = AchievementIconOverrideHelper.GetOverrideValue(unlockedOverrides, apiName);
                    if (!string.IsNullOrWhiteSpace(unlockedSource))
                    {
                        resolvedUnlockedOverride = await _managedCustomIconService
                            .MaterializeCustomIconAsync(
                                unlockedSource,
                                gameId,
                                fileStem,
                                AchievementIconVariant.Unlocked,
                                cancel,
                                overwriteExistingTarget: overwriteExistingTarget)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(resolvedUnlockedOverride))
                        {
                            achievement.UnlockedIconPath = resolvedUnlockedOverride;
                        }
                    }

                    var lockedSource = AchievementIconOverrideHelper.GetOverrideValue(lockedOverrides, apiName);
                    if (!string.IsNullOrWhiteSpace(lockedSource))
                    {
                        resolvedLockedOverride = await _managedCustomIconService
                            .MaterializeCustomIconAsync(
                                lockedSource,
                                gameId,
                                fileStem,
                                AchievementIconVariant.Locked,
                                cancel,
                                overwriteExistingTarget: overwriteExistingTarget)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(resolvedLockedOverride))
                        {
                            achievement.LockedIconPath = resolvedLockedOverride;
                        }
                    }
                }

                // A force-listed achievement whose override was cleared still points at the managed
                // custom file; restore the variant to the still-cached provider default so the stale
                // custom path is not persisted.
                if (forceThisAchievement && !string.IsNullOrWhiteSpace(fileStem))
                {
                    if (string.IsNullOrWhiteSpace(
                        AchievementIconOverrideHelper.GetOverrideValue(unlockedOverrides, apiName)))
                    {
                        achievement.UnlockedIconPath = RestoreDefaultIconPath(
                            achievement.UnlockedIconPath,
                            gameId,
                            fileStem,
                            AchievementIconVariant.Unlocked);
                    }

                    if (string.IsNullOrWhiteSpace(
                        AchievementIconOverrideHelper.GetOverrideValue(lockedOverrides, apiName)))
                    {
                        achievement.LockedIconPath = RestoreDefaultIconPath(
                            achievement.LockedIconPath,
                            gameId,
                            fileStem,
                            AchievementIconVariant.Locked);
                    }
                }

                var unlockedCandidate = ResolveUnlockedSourcePath(achievement);
                achievement.UnlockedIconPath = unlockedCandidate;
                var lockedCandidate = !string.IsNullOrWhiteSpace(resolvedLockedOverride)
                    ? resolvedLockedOverride
                    : achievement.LockedIconPath;
                achievement.LockedIconPath = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                    unlockedCandidate,
                    lockedCandidate,
                    useSeparateLockedIcons,
                    !string.IsNullOrWhiteSpace(resolvedUnlockedOverride),
                    !string.IsNullOrWhiteSpace(resolvedLockedOverride));
            }
        }

        // Restores a cleared override variant to the still-cached provider default (original
        // resolution first, retired compressed cache second - the same order the Icons tab preview
        // uses). Paths that do not point at this game's managed custom icons or a missing file are
        // returned unchanged.
        private string RestoreDefaultIconPath(
            string currentPath,
            string gameId,
            string fileStem,
            AchievementIconVariant variant)
        {
            var needsRestore = string.IsNullOrWhiteSpace(currentPath) ||
                _managedCustomIconService.IsManagedCustomIconPath(currentPath, gameId) ||
                IsMissingLocalFile(currentPath);
            if (!needsRestore)
            {
                return currentPath;
            }

            var restored = _diskImageService.FindExistingAchievementIconCachePath(gameId, fileStem, variant);
            if (!string.IsNullOrWhiteSpace(restored))
            {
                return restored;
            }

            var legacy = _diskImageService.GetLegacyCompressedAchievementIconCachePath(gameId, fileStem, variant);
            return !string.IsNullOrWhiteSpace(legacy) && File.Exists(legacy)
                ? legacy
                : null;
        }

        private static bool IsMissingLocalFile(string path)
        {
            try
            {
                return Path.IsPathRooted(path) && !File.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // Shared achievement-icon pipeline for both owned and friend/provider-only games: build the
        // download requests, download/resolve them, and write the resolved paths back. Callers vary
        // only the output location (resolveTargetPath), whether a separate locked icon is produced,
        // and the unlocked/locked apply policy.
        private async Task PopulateAchievementIconsCoreAsync(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> fileStems,
            string gameId,
            bool buildLockedRequests,
            bool forceRefreshExistingTargets,
            bool reportInitialProgress,
            Func<AchievementDetail, string, AchievementIconVariant, string> resolveTargetPath,
            Func<AchievementDetail, string> resolveUnlockedFallback,
            Func<AchievementDetail, string, string, string> resolveLockedPath,
            CancellationToken cancel,
            Action<int, int> onIconProgress)
        {
            var iconRequests = BuildRequests(
                achievements,
                fileStems,
                gameId,
                buildLockedRequests,
                resolveTargetPath);
            var resolvedPaths = await ResolveIconRequestsAsync(
                    iconRequests,
                    forceRefreshExistingTargets,
                    cancel,
                    onIconProgress,
                    reportInitialProgress)
                .ConfigureAwait(false);

            ApplyResolvedPaths(
                achievements,
                iconRequests,
                resolvedPaths,
                resolveUnlockedFallback,
                resolveLockedPath);
        }

        // Builds the icon-download requests for a set of achievements. The caller supplies the
        // target-path factory (owned games key by Playnite GUID; friends key by provider/game key)
        // and whether a distinct locked-variant request should be produced (friends never do).
        private List<AchievementIconRequest> BuildRequests(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> fileStems,
            string gameId,
            bool buildLockedRequests,
            Func<AchievementDetail, string, AchievementIconVariant, string> resolveTargetPath)
        {
            var requests = new List<AchievementIconRequest>();
            if (achievements == null || achievements.Count == 0 || resolveTargetPath == null)
            {
                return requests;
            }

            for (var i = 0; i < achievements.Count; i++)
            {
                var achievement = achievements[i];
                if (achievement == null || string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                if (!fileStems.TryGetValue(achievement.ApiName.Trim(), out var fileStem) ||
                    string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                AddRequest(
                    requests,
                    achievement,
                    ResolveUnlockedSourcePath(achievement),
                    gameId,
                    fileStem,
                    AchievementIconVariant.Unlocked,
                    resolveTargetPath);

                if (buildLockedRequests)
                {
                    AddRequest(
                        requests,
                        achievement,
                        ResolveDistinctLockedSourcePath(achievement),
                        gameId,
                        fileStem,
                        AchievementIconVariant.Locked,
                        resolveTargetPath);
                }
            }

            return requests;
        }

        private AchievementIconRequest AddStableImageRequest(
            ICollection<AchievementIconRequest> requests,
            string sourcePath,
            string relativeTargetPath)
        {
            if (requests == null ||
                string.IsNullOrWhiteSpace(relativeTargetPath) ||
                !IsSupportedSourcePath(sourcePath))
            {
                return null;
            }

            var request = new AchievementIconRequest
            {
                SourcePath = sourcePath,
                TargetPath = _diskImageService.ResolveCacheRelativePath(relativeTargetPath)
            };
            requests.Add(request);
            return request;
        }

        private async Task<string> PopulateFriendGameImageFileCacheAsync(
            string providerKey,
            string providerGameKey,
            string sourcePath,
            string fileName,
            CancellationToken cancel,
            Action onImageResolved)
        {
            var requests = new List<AchievementIconRequest>();
            var request = AddStableImageRequest(
                requests,
                sourcePath,
                FriendImageCachePathBuilder.BuildGameImageRelativePath(providerKey, providerGameKey, fileName));
            var resolvedPaths = await ResolveIconRequestsAsync(
                    requests,
                    forceRefreshExistingTargets: false,
                    cancel: cancel,
                    onGroupCompleted: onImageResolved)
                .ConfigureAwait(false);

            return TryGetResolvedPath(request, resolvedPaths);
        }

        private async Task<Dictionary<AchievementIconRequest, string>> ResolveIconRequestsAsync(
            IReadOnlyList<AchievementIconRequest> iconRequests,
            bool forceRefreshExistingTargets,
            CancellationToken cancel,
            Action<int, int> onIconProgress = null,
            bool reportInitialProgress = false,
            Action onGroupCompleted = null)
        {
            var resolvedPaths = new Dictionary<AchievementIconRequest, string>();
            if (iconRequests == null || iconRequests.Count == 0)
            {
                return resolvedPaths;
            }

            var groupedRequests = iconRequests
                .Where(request => request != null)
                .GroupBy(request => request.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groupedRequests.Count == 0)
            {
                return resolvedPaths;
            }

            // Only groups that actually need a download (target missing, or a forced refresh) count toward
            // icon-download progress. Groups whose target already exists on disk are resolved silently, so
            // already-cached icons never emit "downloading" progress. onGroupCompleted still fires once per
            // group because its callers track their own separate totals.
            var groupWork = groupedRequests
                .Select(group =>
                {
                    var requests = group.ToList();
                    var needsDownload = forceRefreshExistingTargets ||
                                        string.IsNullOrWhiteSpace(GetFirstExistingTargetPath(requests));
                    return (Requests: requests, NeedsDownload: needsDownload);
                })
                .ToList();

            var downloadTotal = groupWork.Count(work => work.NeedsDownload);
            var iconProgress = new IconDownloadProgress(downloadTotal);
            if (reportInitialProgress && downloadTotal > 0)
            {
                onIconProgress?.Invoke(0, downloadTotal);
            }

            var resolutionTasks = groupWork.Select(async work =>
            {
                var resolved = await ResolveGroupAsync(
                    work.Requests,
                    forceRefreshExistingTargets,
                    cancel).ConfigureAwait(false);

                lock (resolvedPaths)
                {
                    foreach (var pair in resolved)
                    {
                        resolvedPaths[pair.Key] = pair.Value;
                    }
                }

                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                if (work.NeedsDownload && iconProgress.HasWork)
                {
                    var (downloaded, total) = iconProgress.AdvanceAndGetSnapshot();
                    onIconProgress?.Invoke(downloaded, total);
                }

                onGroupCompleted?.Invoke();
            }).ToArray();

            await Task.WhenAll(resolutionTasks).ConfigureAwait(false);
            return resolvedPaths;
        }

        private async Task<Dictionary<AchievementIconRequest, string>> ResolveGroupAsync(
            List<AchievementIconRequest> requests,
            bool forceRefreshExistingTargets,
            CancellationToken cancel)
        {
            var resolved = new Dictionary<AchievementIconRequest, string>();
            if (requests == null || requests.Count == 0)
            {
                return resolved;
            }

            var primaryPath = forceRefreshExistingTargets
                ? null
                : GetFirstExistingTargetPath(requests);

            if (string.IsNullOrWhiteSpace(primaryPath))
            {
                var primaryRequest = requests[0];
                primaryPath = await ResolvePrimaryPathAsync(
                    primaryRequest.SourcePath,
                    primaryRequest.TargetPath,
                    forceRefreshExistingTargets,
                    cancel).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(primaryPath) || !File.Exists(primaryPath))
            {
                return resolved;
            }

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var finalPath = ResolveEffectiveTargetPath(request);

                if (!string.Equals(primaryPath, finalPath, StringComparison.OrdinalIgnoreCase) &&
                    (!File.Exists(finalPath) || forceRefreshExistingTargets))
                {
                    finalPath = await _diskImageService
                        .CopyCachedIconAsync(primaryPath, finalPath, cancel, overwriteExistingTarget: forceRefreshExistingTargets)
                        .ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
                {
                    continue;
                }

                resolved[request] = finalPath;
            }

            return resolved;
        }

        // Icons are always cached at original resolution and format (decode size 0 disables the
        // retired downscale/re-encode path in DiskImageService).
        private static string ResolveEffectiveTargetPath(AchievementIconRequest request)
        {
            return request == null
                ? null
                : DiskImageService.ResolveTargetPathForSource(request.TargetPath, request.SourcePath, decodeSize: 0);
        }

        private async Task<string> ResolvePrimaryPathAsync(
            string sourcePath,
            string targetPath,
            bool forceRefreshExistingTargets,
            CancellationToken cancel)
        {
            if (!IsSupportedSourcePath(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            try
            {
                if (IsHttpIconPath(sourcePath))
                {
                    return await _diskImageService
                        .GetOrDownloadIconToPathAsync(
                            sourcePath,
                            targetPath,
                            decodeSize: 0,
                            cancel: cancel,
                            overwriteExistingTarget: forceRefreshExistingTargets)
                        .ConfigureAwait(false);
                }

                return await _diskImageService
                    .GetOrCopyLocalIconToPathAsync(
                        sourcePath,
                        targetPath,
                        decodeSize: 0,
                        cancel: cancel,
                        overwriteExistingTarget: forceRefreshExistingTargets)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, string.Format(
                    "Failed to resolve achievement icon path for {0}.",
                    sourcePath));
                return null;
            }
        }

        // Writes resolved icon paths back onto the achievements. The unlocked fallback (used when
        // no request resolved) and the locked-path policy differ between owned games (custom-icon
        // overrides + separate-locked setting) and friends (locked mirrors unlocked).
        private void ApplyResolvedPaths(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyList<AchievementIconRequest> requests,
            IReadOnlyDictionary<AchievementIconRequest, string> resolvedPaths,
            Func<AchievementDetail, string> resolveUnlockedFallback,
            Func<AchievementDetail, string, string, string> resolveLockedPath)
        {
            var requestsByAchievement = (requests ?? Array.Empty<AchievementIconRequest>())
                .Where(request => request?.Achievement != null)
                .GroupBy(request => request.Achievement)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList());

            foreach (var achievement in achievements ?? Array.Empty<AchievementDetail>())
            {
                if (achievement == null)
                {
                    continue;
                }

                requestsByAchievement.TryGetValue(achievement, out var achievementRequests);

                var unlockedResolved = GetResolvedPathForVariant(
                    achievementRequests,
                    AchievementIconVariant.Unlocked,
                    resolvedPaths);
                var finalUnlockedPath = !string.IsNullOrWhiteSpace(unlockedResolved)
                    ? unlockedResolved
                    : resolveUnlockedFallback(achievement);
                achievement.UnlockedIconPath = finalUnlockedPath;

                var resolvedLockedCandidate = GetResolvedPathForVariant(
                    achievementRequests,
                    AchievementIconVariant.Locked,
                    resolvedPaths);
                achievement.LockedIconPath = resolveLockedPath(
                    achievement,
                    finalUnlockedPath,
                    resolvedLockedCandidate);
            }
        }

        // Owned locked-path policy: honor a distinct managed custom locked icon and the
        // separate-locked-icons setting, otherwise fall back to the unlocked icon.
        private string ResolveOwnedLockedPath(
            AchievementDetail achievement,
            string finalUnlockedPath,
            string resolvedLockedCandidate,
            string gameId,
            bool useSeparateLockedIcons)
        {
            var finalLockedCandidate = resolvedLockedCandidate ?? achievement.LockedIconPath;
            var hasExplicitUnlockedIcon = _managedCustomIconService.IsManagedCustomIconPath(
                finalUnlockedPath,
                gameId);
            var hasExplicitLockedIcon = _managedCustomIconService.IsManagedCustomIconPath(
                    finalLockedCandidate,
                    gameId) &&
                HasDistinctLockedSource(finalUnlockedPath, finalLockedCandidate);
            return AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                finalUnlockedPath,
                finalLockedCandidate,
                useSeparateLockedIcons,
                hasExplicitUnlockedIcon,
                hasExplicitLockedIcon);
        }

        private void AddRequest(
            ICollection<AchievementIconRequest> requests,
            AchievementDetail achievement,
            string sourcePath,
            string gameId,
            string fileStem,
            AchievementIconVariant variant,
            Func<AchievementDetail, string, AchievementIconVariant, string> resolveTargetPath)
        {
            if (requests == null ||
                achievement == null ||
                resolveTargetPath == null ||
                string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(fileStem))
            {
                return;
            }

            // Owned games skip sources that are already managed custom icons; friend sources are
            // provider URLs, for which this check is a harmless no-op (gameId is null there).
            if (_managedCustomIconService.IsManagedCustomIconPath(sourcePath, gameId))
            {
                return;
            }

            var targetPath = resolveTargetPath(achievement, fileStem, variant);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            requests.Add(new AchievementIconRequest
            {
                Achievement = achievement,
                Variant = variant,
                SourcePath = sourcePath,
                TargetPath = targetPath
            });
        }

        private static string TryGetResolvedPath(
            AchievementIconRequest request,
            IReadOnlyDictionary<AchievementIconRequest, string> resolvedPaths)
        {
            if (request == null || resolvedPaths == null)
            {
                return null;
            }

            return resolvedPaths.TryGetValue(request, out var resolvedPath)
                ? resolvedPath
                : ResolveExistingTargetPath(request);
        }

        private static string ResolveExistingTargetPath(AchievementIconRequest request)
        {
            if (request == null)
            {
                return null;
            }

            var effectiveTargetPath = ResolveEffectiveTargetPath(request);
            if (!string.IsNullOrWhiteSpace(effectiveTargetPath) && File.Exists(effectiveTargetPath))
            {
                return effectiveTargetPath;
            }

            return File.Exists(request.TargetPath) ? request.TargetPath : null;
        }

        private static string GetResolvedPathForVariant(
            IReadOnlyList<AchievementIconRequest> requests,
            AchievementIconVariant variant,
            IReadOnlyDictionary<AchievementIconRequest, string> resolvedPaths)
        {
            if (requests == null || requests.Count == 0)
            {
                return null;
            }

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                if (request?.Variant != variant)
                {
                    continue;
                }

                var resolvedPath = TryGetResolvedPath(request, resolvedPaths);
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    return resolvedPath;
                }
            }

            return null;
        }

        private static string GetFirstExistingTargetPath(IReadOnlyList<AchievementIconRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return null;
            }

            for (var i = 0; i < requests.Count; i++)
            {
                var targetPath = ResolveEffectiveTargetPath(requests[i]);
                if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
                {
                    return targetPath;
                }
            }

            return null;
        }

        private static string ResolveGameId(GameAchievementData data)
        {
            if (data?.PlayniteGameId != null && data.PlayniteGameId.Value != Guid.Empty)
            {
                return data.PlayniteGameId.Value.ToString("D");
            }

            return Guid.Empty.ToString("D");
        }

        private static string ResolveUnlockedSourcePath(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return null;
            }

            if (IsSupportedSourcePath(achievement.UnlockedIconPath))
            {
                return achievement.UnlockedIconPath;
            }

            return IsSupportedSourcePath(achievement.LockedIconPath)
                ? achievement.LockedIconPath
                : null;
        }

        private static string ResolveDistinctLockedSourcePath(
            AchievementDetail achievement)
        {
            if (achievement == null ||
                !HasDistinctLockedSource(achievement.UnlockedIconPath, achievement.LockedIconPath) ||
                !IsSupportedSourcePath(achievement.LockedIconPath))
            {
                return null;
            }

            return achievement.LockedIconPath;
        }

        private static bool HasDistinctLockedSource(string unlockedIconPath, string lockedIconPath)
        {
            if (string.IsNullOrWhiteSpace(lockedIconPath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(unlockedIconPath))
            {
                return true;
            }

            return !string.Equals(
                unlockedIconPath.Trim(),
                lockedIconPath.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedSourcePath(string iconPath)
        {
            return DiskImageService.IsCacheableImageSource(iconPath);
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static bool IsHttpIconPath(string iconPath)
        {
            return !string.IsNullOrWhiteSpace(iconPath) &&
                   (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

    }
}
