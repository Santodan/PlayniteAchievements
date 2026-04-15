using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
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
    /// <summary>
    /// Handles icon path resolution and local icon cache population for achievements.
    /// </summary>
    public class AchievementIconService
    {
        private const int OptimizedDecodeSize = 128;

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

        /// <summary>
        /// Downloads and caches achievement icons for a GameAchievementData object.
        /// Updates icon paths in-place to point to local cached files.
        /// </summary>
        public Task DownloadAchievementIconsAsync(
            GameAchievementData data,
            CancellationToken cancel = default)
        {
            return DownloadAchievementIconsAsync(data, null, null, cancel);
        }

        public Task DownloadAchievementIconsAsync(
            GameAchievementData data,
            IReadOnlyDictionary<string, string> unlockedOverrides,
            IReadOnlyDictionary<string, string> lockedOverrides,
            CancellationToken cancel = default)
        {
            return PopulateAchievementIconCacheAsync(data, unlockedOverrides, lockedOverrides, cancel);
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
            bool forceRefreshExistingTargets = false)
        {
            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var preserveOriginalResolution = _settings.PreserveAchievementIconResolution;
            var useSeparateLockedIcons = GameCustomDataLookup.ShouldUseSeparateLockedIcons(data?.PlayniteGameId, _settings);
            var decodeSize = preserveOriginalResolution ? 0 : OptimizedDecodeSize;
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
                    cancel)
                .ConfigureAwait(false);

            var iconRequests = BuildRequests(
                data.Achievements,
                fileStems,
                gameId,
                preserveOriginalResolution);

            var resolvedPaths = new Dictionary<AchievementIconRequest, string>();
            if (iconRequests.Count > 0)
            {
                var groupedRequests = iconRequests
                    .GroupBy(request => request.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var iconProgress = new IconDownloadProgress(groupedRequests.Count);
                var resolutionTasks = groupedRequests.Select(async group =>
                {
                    var resolved = await ResolveGroupAsync(
                        group.ToList(),
                        decodeSize,
                        !preserveOriginalResolution,
                        gameId,
                        forceRefreshExistingTargets,
                        cancel).ConfigureAwait(false);

                    lock (resolvedPaths)
                    {
                        foreach (var pair in resolved)
                        {
                            resolvedPaths[pair.Key] = pair.Value;
                        }
                    }

                    if (iconProgress.HasWork && !cancel.IsCancellationRequested)
                    {
                        var (downloaded, total) = iconProgress.AdvanceAndGetSnapshot();
                        onIconProgress?.Invoke(downloaded, total);
                    }
                }).ToArray();

                await Task.WhenAll(resolutionTasks).ConfigureAwait(false);
            }

            ApplyResolvedPaths(
                data.Achievements,
                iconRequests,
                resolvedPaths,
                useSeparateLockedIcons,
                gameId);
        }

        private async Task PrepareSourcePathsAsync(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> fileStems,
            string gameId,
            bool useSeparateLockedIcons,
            IReadOnlyDictionary<string, string> unlockedOverrides,
            IReadOnlyDictionary<string, string> lockedOverrides,
            bool overwriteExistingTargets,
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

                var resolvedUnlockedOverride = default(string);
                var resolvedLockedOverride = default(string);

                if (hasOverrides &&
                    !string.IsNullOrWhiteSpace(apiName) &&
                    fileStems.TryGetValue(apiName, out var fileStem) &&
                    !string.IsNullOrWhiteSpace(fileStem))
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
                                overwriteExistingTarget: overwriteExistingTargets)
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
                                overwriteExistingTarget: overwriteExistingTargets)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(resolvedLockedOverride))
                        {
                            achievement.LockedIconPath = resolvedLockedOverride;
                        }
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

        private List<AchievementIconRequest> BuildRequests(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> fileStems,
            string gameId,
            bool preserveOriginalResolution)
        {
            var requests = new List<AchievementIconRequest>();
            if (achievements == null || achievements.Count == 0)
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
                    preserveOriginalResolution,
                    fileStem,
                    AchievementIconVariant.Unlocked);

                AddRequest(
                    requests,
                    achievement,
                    ResolveDistinctLockedSourcePath(achievement),
                    gameId,
                    preserveOriginalResolution,
                    fileStem,
                    AchievementIconVariant.Locked);
            }

            return requests;
        }

        private async Task<Dictionary<AchievementIconRequest, string>> ResolveGroupAsync(
            List<AchievementIconRequest> requests,
            int decodeSize,
            bool allowLegacy128Migration,
            string gameId,
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
                if (!forceRefreshExistingTargets &&
                    allowLegacy128Migration &&
                    _diskImageService.TryMigrateLegacyAchievementIcon(
                        primaryRequest.SourcePath,
                        primaryRequest.TargetPath,
                        OptimizedDecodeSize,
                        gameId))
                {
                    primaryPath = primaryRequest.TargetPath;
                }
                else
                {
                    primaryPath = await ResolvePrimaryPathAsync(
                        primaryRequest.SourcePath,
                        primaryRequest.TargetPath,
                        decodeSize,
                        forceRefreshExistingTargets,
                        cancel).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(primaryPath) || !File.Exists(primaryPath))
            {
                return resolved;
            }

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var finalPath = request.TargetPath;

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

        private async Task<string> ResolvePrimaryPathAsync(
            string sourcePath,
            string targetPath,
            int decodeSize,
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
                            decodeSize,
                            cancel,
                            overwriteExistingTarget: forceRefreshExistingTargets)
                        .ConfigureAwait(false);
                }

                return await _diskImageService
                    .GetOrCopyLocalIconToPathAsync(
                        sourcePath,
                        targetPath,
                        decodeSize,
                        cancel,
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

        private void ApplyResolvedPaths(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyList<AchievementIconRequest> requests,
            IReadOnlyDictionary<AchievementIconRequest, string> resolvedPaths,
            bool useSeparateLockedIcons,
            string gameId)
        {
            var requestsByAchievement = requests
                .GroupBy(request => request.Achievement)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToList());

            for (var i = 0; i < achievements.Count; i++)
            {
                var achievement = achievements[i];
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
                    : achievement.UnlockedIconPath;
                achievement.UnlockedIconPath = finalUnlockedPath;

                var finalLockedCandidate = GetResolvedPathForVariant(
                        achievementRequests,
                        AchievementIconVariant.Locked,
                        resolvedPaths) ?? achievement.LockedIconPath;
                var hasExplicitUnlockedIcon = _managedCustomIconService.IsManagedCustomIconPath(
                    finalUnlockedPath,
                    gameId);
                var hasExplicitLockedIcon = _managedCustomIconService.IsManagedCustomIconPath(
                        finalLockedCandidate,
                        gameId) &&
                    HasDistinctLockedSource(finalUnlockedPath, finalLockedCandidate);
                achievement.LockedIconPath = AchievementIconOverrideHelper.ResolveEffectiveLockedPath(
                    finalUnlockedPath,
                    finalLockedCandidate,
                    useSeparateLockedIcons,
                    hasExplicitUnlockedIcon,
                    hasExplicitLockedIcon);
            }
        }

        private void AddRequest(
            ICollection<AchievementIconRequest> requests,
            AchievementDetail achievement,
            string sourcePath,
            string gameId,
            bool preserveOriginalResolution,
            string fileStem,
            AchievementIconVariant variant)
        {
            if (requests == null ||
                achievement == null ||
                string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(fileStem))
            {
                return;
            }

            if (_managedCustomIconService.IsManagedCustomIconPath(sourcePath, gameId))
            {
                return;
            }

            requests.Add(new AchievementIconRequest
            {
                Achievement = achievement,
                Variant = variant,
                SourcePath = sourcePath,
                TargetPath = _diskImageService.GetAchievementIconCachePath(
                    gameId,
                    preserveOriginalResolution,
                    fileStem,
                    variant)
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
                : (File.Exists(request.TargetPath) ? request.TargetPath : null);
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
                var targetPath = requests[i]?.TargetPath;
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
            return IsHttpIconPath(iconPath) || DiskImageService.IsLocalIconPath(iconPath);
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
