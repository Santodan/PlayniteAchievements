using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Images
{
    public sealed class ManagedCustomIconService
    {
        private readonly DiskImageService _diskImageService;
        private readonly ILogger _logger;

        public ManagedCustomIconService(DiskImageService diskImageService, ILogger logger = null)
        {
            _diskImageService = diskImageService ?? throw new ArgumentNullException(nameof(diskImageService));
            _logger = logger;
        }

        public string GetGameCustomIconDirectory(string gameId)
        {
            var trimmedGameId = string.IsNullOrWhiteSpace(gameId)
                ? Guid.Empty.ToString("D")
                : gameId.Trim();
            return Path.Combine(
                _diskImageService.GetCacheDirectoryPath(),
                trimmedGameId,
                AchievementIconCachePathBuilder.GetCustomFolder());
        }

        public string GetAchievementCustomIconPath(
            string gameId,
            string fileStem,
            AchievementIconVariant variant)
        {
            var relativePath = AchievementIconCachePathBuilder.BuildCustomRelativePath(
                gameId,
                fileStem,
                variant);
            return Path.Combine(
                Path.GetDirectoryName(_diskImageService.GetCacheDirectoryPath()) ?? string.Empty,
                relativePath);
        }

        public bool IsManagedCustomIconPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var normalizedPath = Path.GetFullPath(path);
                var cacheRoot = Path.GetFullPath(_diskImageService.GetCacheDirectoryPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!IsPathWithinDirectory(normalizedPath, cacheRoot))
                {
                    return false;
                }

                var directory = new DirectoryInfo(Path.GetDirectoryName(normalizedPath) ?? string.Empty);
                while (directory != null)
                {
                    if (string.Equals(
                        directory.Name,
                        AchievementIconCachePathBuilder.GetCustomFolder(),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (string.Equals(
                        directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        cacheRoot,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    directory = directory.Parent;
                }
            }
            catch
            {
            }

            return false;
        }

        public bool IsManagedCustomIconPath(string path, string gameId)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var normalizedPath = Path.GetFullPath(path);
                var gameCustomDirectory = Path.GetFullPath(GetGameCustomIconDirectory(gameId))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return IsPathWithinDirectory(normalizedPath, gameCustomDirectory);
            }
            catch
            {
                return false;
            }
        }

        public string GetManagedDisplayPath(string path, string gameId)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized) || !IsManagedCustomIconPath(normalized, gameId))
            {
                return normalized;
            }

            try
            {
                var baseDirectory = GetCacheRootDirectory();
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return normalized;
                }

                var candidate = Path.GetFullPath(normalized)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var baseWithSeparator = EnsureTrailingSeparator(baseDirectory);
                if (!candidate.StartsWith(baseWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }

                return candidate.Substring(baseWithSeparator.Length);
            }
            catch
            {
                return normalized;
            }
        }

        public string ResolveManagedDisplayPath(string value, string gameId)
        {
            var normalized = NormalizePath(value);
            if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
            {
                return normalized;
            }

            if (!normalized.StartsWith("icon_cache", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            try
            {
                var baseDirectory = GetCacheRootDirectory();
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return normalized;
                }

                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
                return IsManagedCustomIconPath(candidate, gameId)
                    ? candidate
                    : normalized;
            }
            catch
            {
                return normalized;
            }
        }

        public async Task<string> MaterializeCustomIconAsync(
            string sourcePath,
            string gameId,
            string fileStem,
            AchievementIconVariant variant,
            CancellationToken cancel,
            bool overwriteExistingTarget = false)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(fileStem))
            {
                return null;
            }

            var targetPath = GetAchievementCustomIconPath(gameId, fileStem, variant);
            if (string.Equals(sourcePath.Trim(), targetPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(targetPath))
            {
                return targetPath;
            }

            if (IsHttpUrl(sourcePath))
            {
                return await _diskImageService
                    .GetOrDownloadIconToPathAsync(
                        sourcePath,
                        targetPath,
                        decodeSize: 0,
                        cancel,
                        overwriteExistingTarget: overwriteExistingTarget)
                    .ConfigureAwait(false);
            }

            if (!File.Exists(sourcePath))
            {
                return null;
            }

            return await _diskImageService
                .GetOrCopyLocalIconToPathAsync(
                    sourcePath,
                    targetPath,
                    decodeSize: 0,
                    cancel,
                    overwriteExistingTarget: overwriteExistingTarget)
                .ConfigureAwait(false);
        }

        public void ClearGameCustomCache(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return;
            }

            try
            {
                var customDirectory = GetGameCustomIconDirectory(gameId);
                if (Directory.Exists(customDirectory))
                {
                    Directory.Delete(customDirectory, recursive: true);
                }

                DeleteEmptyDirectories(Path.Combine(_diskImageService.GetCacheDirectoryPath(), gameId.Trim()));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to clear managed custom icon cache for game '{gameId}'.");
            }
        }

        public void PruneGameCustomCache(string gameId, IEnumerable<string> retainedPaths)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return;
            }

            var customDirectory = GetGameCustomIconDirectory(gameId);
            if (!Directory.Exists(customDirectory))
            {
                return;
            }

            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in retainedPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    var normalized = Path.GetFullPath(path);
                    if (IsPathWithinDirectory(normalized, Path.GetFullPath(customDirectory)))
                    {
                        retained.Add(normalized);
                    }
                }
                catch
                {
                }
            }

            foreach (var file in Directory.EnumerateFiles(customDirectory, "*.png", SearchOption.AllDirectories))
            {
                try
                {
                    var normalized = Path.GetFullPath(file);
                    if (retained.Contains(normalized))
                    {
                        continue;
                    }

                    File.Delete(normalized);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed pruning managed custom icon '{file}'.");
                }
            }

            DeleteEmptyDirectories(customDirectory);
            DeleteEmptyDirectories(Path.Combine(_diskImageService.GetCacheDirectoryPath(), gameId.Trim()));
        }

        private static bool IsPathWithinDirectory(string candidatePath, string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
            {
                return false;
            }

            if (string.Equals(candidatePath, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedDirectory = directoryPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directoryPath
                : directoryPath + Path.DirectorySeparatorChar;

            return candidatePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private string GetCacheRootDirectory()
        {
            return Path.GetDirectoryName(Path.GetFullPath(_diskImageService.GetCacheDirectoryPath()));
        }

        private static string NormalizePath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string EnsureTrailingSeparator(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return directoryPath;
            }

            return directoryPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directoryPath
                : directoryPath + Path.DirectorySeparatorChar;
        }

        private static void DeleteEmptyDirectories(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories(rootDirectory, "*", SearchOption.AllDirectories))
            {
                DeleteEmptyDirectories(directory);
            }

            if (Directory.GetDirectories(rootDirectory).Length == 0 &&
                Directory.GetFiles(rootDirectory).Length == 0)
            {
                Directory.Delete(rootDirectory, recursive: false);
            }
        }

        private static bool IsHttpUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) &&
                   (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
