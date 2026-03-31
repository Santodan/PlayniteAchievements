using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
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
        private readonly PersistedSettings _settings;
        private readonly ILogger _logger;

        public AchievementIconService(DiskImageService diskImageService, PersistedSettings settings, ILogger logger)
        {
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
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
            return PopulateAchievementIconCacheAsync(data, cancel);
        }

        public async Task PopulateAchievementIconCacheAsync(
            GameAchievementData data,
            CancellationToken cancel,
            Action<int, int> onIconProgress = null)
        {
            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var preserveOriginalResolution = _settings.PreserveAchievementIconResolution;
            var useSeparateLockedIcons = _settings.ShouldUseSeparateLockedIcons(data?.PlayniteGameId);
            var decodeSize = preserveOriginalResolution ? 0 : OptimizedDecodeSize;
            var gameId = ResolveGameId(data);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                data.Achievements.Select(achievement => achievement?.ApiName));

            var iconRequests = BuildRequests(
                data.Achievements,
                fileStems,
                gameId,
                preserveOriginalResolution,
                useSeparateLockedIcons);

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

            ApplyResolvedPaths(data.Achievements, iconRequests, resolvedPaths, useSeparateLockedIcons);
        }

        private List<AchievementIconRequest> BuildRequests(
            IReadOnlyList<AchievementDetail> achievements,
            IReadOnlyDictionary<string, string> fileStems,
            string gameId,
            bool preserveOriginalResolution,
            bool useSeparateLockedIcons)
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
                    ResolveDistinctLockedSourcePath(achievement, useSeparateLockedIcons),
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
            CancellationToken cancel)
        {
            var resolved = new Dictionary<AchievementIconRequest, string>();
            if (requests == null || requests.Count == 0)
            {
                return resolved;
            }

            var primaryPath = GetFirstExistingTargetPath(requests);

            if (string.IsNullOrWhiteSpace(primaryPath))
            {
                var primaryRequest = requests[0];
                if (allowLegacy128Migration &&
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
                    !File.Exists(finalPath))
                {
                    finalPath = await _diskImageService
                        .CopyCachedIconAsync(primaryPath, finalPath, cancel)
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
                        .GetOrDownloadIconToPathAsync(sourcePath, targetPath, decodeSize, cancel)
                        .ConfigureAwait(false);
                }

                return await _diskImageService
                    .GetOrCopyLocalIconToPathAsync(sourcePath, targetPath, decodeSize, cancel)
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
            bool useSeparateLockedIcons)
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

                if (!string.IsNullOrWhiteSpace(unlockedResolved))
                {
                    achievement.UnlockedIconPath = unlockedResolved;
                }

                achievement.LockedIconPath = ResolveLockedPath(
                    achievement.UnlockedIconPath,
                    GetResolvedPathForVariant(
                        achievementRequests,
                        AchievementIconVariant.Locked,
                        resolvedPaths),
                    useSeparateLockedIcons);
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
            AchievementDetail achievement,
            bool useSeparateLockedIcons)
        {
            if (!useSeparateLockedIcons ||
                achievement == null ||
                !HasDistinctLockedSource(achievement.UnlockedIconPath, achievement.LockedIconPath) ||
                !IsSupportedSourcePath(achievement.LockedIconPath))
            {
                return null;
            }

            return achievement.LockedIconPath;
        }

        private static string ResolveLockedPath(
            string unlockedIconPath,
            string lockedIconPath,
            bool useSeparateLockedIcons)
        {
            if (!useSeparateLockedIcons)
            {
                return unlockedIconPath;
            }

            return !string.IsNullOrWhiteSpace(lockedIconPath)
                ? lockedIconPath
                : unlockedIconPath;
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

        private static bool IsHttpIconPath(string iconPath)
        {
            return !string.IsNullOrWhiteSpace(iconPath) &&
                   (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

    }
}
