using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
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
        private readonly DiskImageService _diskImageService;
        private readonly ILogger _logger;

        public AchievementIconService(DiskImageService diskImageService, ILogger logger)
        {
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
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

            var gameIdStr = data.PlayniteGameId?.ToString();
            var groupedByIcon = new Dictionary<string, List<AchievementDetail>>(StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in data.Achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var iconPath = achievement.UnlockedIconPath;
                if (!IsHttpIconPath(iconPath) && !IsLocalIconPath(iconPath))
                {
                    continue;
                }

                if (!groupedByIcon.TryGetValue(iconPath, out var grouped))
                {
                    grouped = new List<AchievementDetail>();
                    groupedByIcon[iconPath] = grouped;
                }

                grouped.Add(achievement);
            }

            if (groupedByIcon.Count == 0)
            {
                return;
            }

            const int iconDecodeSize = 128;

            var cachedIconPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var iconsToProcess = new List<string>();

            foreach (var iconPath in groupedByIcon.Keys)
            {
                var cachedPath = _diskImageService.GetIconCachePathFromUri(iconPath, iconDecodeSize, gameIdStr);
                if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                {
                    cachedIconPaths[iconPath] = cachedPath;
                    continue;
                }

                iconsToProcess.Add(iconPath);
            }

            foreach (var kvp in cachedIconPaths)
            {
                if (groupedByIcon.TryGetValue(kvp.Key, out var grouped))
                {
                    foreach (var achievement in grouped)
                    {
                        achievement.UnlockedIconPath = kvp.Value;
                    }
                }
            }

            if (iconsToProcess.Count == 0)
            {
                return;
            }

            var iconProgress = new IconDownloadProgress(iconsToProcess.Count);

            var iconTasks = iconsToProcess.Select(async iconPath =>
            {
                var result = await ResolveIconPathAsync(iconPath, gameIdStr, cancel).ConfigureAwait(false);

                if (iconProgress.HasWork && !cancel.IsCancellationRequested)
                {
                    var (downloaded, total) = iconProgress.AdvanceAndGetSnapshot();
                    onIconProgress?.Invoke(downloaded, total);
                }

                return result;
            }).ToArray();

            var resolvedIconPaths = await Task.WhenAll(iconTasks).ConfigureAwait(false);
            foreach (var resolved in resolvedIconPaths)
            {
                if (string.IsNullOrWhiteSpace(resolved.OriginalPath) ||
                    string.IsNullOrWhiteSpace(resolved.LocalPath))
                {
                    continue;
                }

                if (!groupedByIcon.TryGetValue(resolved.OriginalPath, out var grouped))
                {
                    continue;
                }

                foreach (var achievement in grouped)
                {
                    achievement.UnlockedIconPath = resolved.LocalPath;
                }
            }
        }

        private async Task<(string OriginalPath, string LocalPath)> ResolveIconPathAsync(
            string originalPath,
            string gameIdStr,
            CancellationToken cancel)
        {
            if (!IsHttpIconPath(originalPath) && !IsLocalIconPath(originalPath))
            {
                return default;
            }

            const int iconDecodeSize = 128;

            try
            {
                if (_diskImageService.IsIconCached(originalPath, iconDecodeSize, gameIdStr))
                {
                    var cachedPath = _diskImageService.GetIconCachePathFromUri(originalPath, iconDecodeSize, gameIdStr);
                    if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                    {
                        return (originalPath, cachedPath);
                    }
                }

                string localPath;
                if (IsHttpIconPath(originalPath))
                {
                    localPath = await _diskImageService
                        .GetOrDownloadIconAsync(originalPath, iconDecodeSize, cancel, gameIdStr)
                        .ConfigureAwait(false);
                }
                else
                {
                    localPath = await _diskImageService
                        .GetOrCopyLocalIconAsync(originalPath, iconDecodeSize, cancel, gameIdStr)
                        .ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    return (originalPath, localPath);
                }

                return default;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, string.Format(
                    "Failed to resolve achievement icon path for {0}.",
                    originalPath));
                return default;
            }
        }

        private static bool IsHttpIconPath(string iconPath)
        {
            return !string.IsNullOrWhiteSpace(iconPath) &&
                   (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsLocalIconPath(string iconPath)
        {
            return !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath);
        }
    }
}