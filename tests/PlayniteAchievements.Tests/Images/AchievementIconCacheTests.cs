using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images.Tests
{
    [TestClass]
    public class AchievementIconCacheTests
    {
        [TestMethod]
        public void BuildRelativePath_UsesApiNameVariantAndModeFolder()
        {
            var stems = AchievementIconCachePathBuilder.BuildFileStems(new[] { " boss:win " });
            var stem = stems["boss:win"];

            var unlockedPath = AchievementIconCachePathBuilder.BuildRelativePath(
                "game-123",
                preserveOriginalResolution: false,
                stem,
                AchievementIconVariant.Unlocked);
            var lockedPath = AchievementIconCachePathBuilder.BuildRelativePath(
                "game-123",
                preserveOriginalResolution: true,
                stem,
                AchievementIconVariant.Locked);

            Assert.AreEqual(Path.Combine("icon_cache", "game-123", "128", "boss_win.png"), unlockedPath);
            Assert.AreEqual(Path.Combine("icon_cache", "game-123", "original", "boss_win.locked.png"), lockedPath);
        }

        [TestMethod]
        public void BuildFileStems_AppendsSuffixesForSanitizedCollisions()
        {
            var stems = AchievementIconCachePathBuilder.BuildFileStems(new[] { "boss:win", "boss/win", "CON" });

            Assert.AreEqual(3, stems.Count);
            Assert.AreNotEqual(stems["boss:win"], stems["boss/win"]);
            StringAssert.StartsWith(stems["boss:win"], "boss_win_");
            StringAssert.StartsWith(stems["boss/win"], "boss_win_");
            Assert.AreEqual("CON_", stems["CON"]);
        }

        [TestMethod]
        public void GetLockedDisplayIcon_UsesExplicitLockedFileWithoutGrayscale()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var unlockedPath = Path.Combine(tempDir, "unlocked.png");
                var lockedPath = Path.Combine(tempDir, "locked.png");
                WritePlaceholderFile(unlockedPath);
                WritePlaceholderFile(lockedPath);

                var displayPath = AchievementIconResolver.GetLockedDisplayIcon(unlockedPath, lockedPath);

                Assert.AreEqual(lockedPath, displayPath);
                Assert.IsTrue(AchievementIconResolver.HasExplicitLockedIcon(lockedPath, unlockedPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void GetLockedDisplayIcon_FallsBackToGrayUnlockedWhenLockedIconIsUnavailable()
        {
            var unlockedPath = @"C:\icons\unlocked.png";
            var remoteLockedPath = "https://cdn.example.com/locked.png";

            var displayPath = AchievementIconResolver.GetLockedDisplayIcon(unlockedPath, remoteLockedPath);

            Assert.AreEqual("gray:" + unlockedPath, displayPath);
            Assert.IsFalse(AchievementIconResolver.HasExplicitLockedIcon(remoteLockedPath, unlockedPath));
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_MigratesLegacyHashCacheToApiNamePath()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var settings = new PersistedSettings
                {
                    PreserveAchievementIconResolution = false,
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(diskImageService, settings, logger: null);
                var gameId = Guid.NewGuid();
                var sourcePath = "https://cdn.example.com/icons/legacy.png";
                var achievement = new AchievementDetail
                {
                    ApiName = "legacy:achievement",
                    UnlockedIconPath = sourcePath,
                    LockedIconPath = sourcePath
                };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { achievement }
                };

                var legacyPath = diskImageService.GetIconCachePathFromUri(sourcePath, 128, gameId.ToString("D"));
                WritePlaceholderFile(legacyPath);

                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { achievement.ApiName })[achievement.ApiName];
                var apiNamedPath = diskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Unlocked);

                await iconService.PopulateAchievementIconCacheAsync(data, CancellationToken.None);

                Assert.IsFalse(File.Exists(legacyPath));
                Assert.IsTrue(File.Exists(apiNamedPath));
                Assert.AreEqual(apiNamedPath, achievement.UnlockedIconPath);
                Assert.AreEqual(apiNamedPath, achievement.LockedIconPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_UsesSeparateLockedIconsOnlyWhenEnabled()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var unlockedSource = "https://cdn.example.com/icons/unlocked.png";
                var lockedSource = "https://cdn.example.com/icons/locked.png";
                var apiName = "shared_icon";
                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];

                var enabledSettings = new PersistedSettings
                {
                    PreserveAchievementIconResolution = false,
                    UseSeparateLockedIconsWhenAvailable = true
                };
                var enabledDiskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var enabledService = new AchievementIconService(enabledDiskImageService, enabledSettings, logger: null);
                var unlockedTarget = enabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Unlocked);
                var lockedTarget = enabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Locked);
                WritePlaceholderFile(unlockedTarget);
                WritePlaceholderFile(lockedTarget);

                var enabledAchievement = new AchievementDetail
                {
                    ApiName = apiName,
                    UnlockedIconPath = unlockedSource,
                    LockedIconPath = lockedSource
                };
                var enabledData = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { enabledAchievement }
                };

                await enabledService.PopulateAchievementIconCacheAsync(enabledData, CancellationToken.None);

                Assert.AreEqual(unlockedTarget, enabledAchievement.UnlockedIconPath);
                Assert.AreEqual(lockedTarget, enabledAchievement.LockedIconPath);
                Assert.AreNotEqual(enabledAchievement.UnlockedIconPath, enabledAchievement.LockedIconPath);

                var disabledSettings = new PersistedSettings
                {
                    PreserveAchievementIconResolution = false,
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var disabledDiskImageService = new DiskImageService(logger: null, cacheRoot: Path.Combine(tempDir, "disabled"));
                var disabledService = new AchievementIconService(disabledDiskImageService, disabledSettings, logger: null);
                var disabledUnlockedTarget = disabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Unlocked);
                WritePlaceholderFile(disabledUnlockedTarget);

                var disabledAchievement = new AchievementDetail
                {
                    ApiName = apiName,
                    UnlockedIconPath = unlockedSource,
                    LockedIconPath = lockedSource
                };
                var disabledData = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { disabledAchievement }
                };

                await disabledService.PopulateAchievementIconCacheAsync(disabledData, CancellationToken.None);

                Assert.AreEqual(disabledUnlockedTarget, disabledAchievement.UnlockedIconPath);
                Assert.AreEqual(disabledUnlockedTarget, disabledAchievement.LockedIconPath);
                Assert.IsFalse(File.Exists(disabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Locked)));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_UsesSeparateLockedIconsForPerGameOverride()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var unlockedSource = "https://cdn.example.com/icons/unlocked.png";
                var lockedSource = "https://cdn.example.com/icons/locked.png";
                var apiName = "override_icon";
                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];

                var settings = new PersistedSettings
                {
                    PreserveAchievementIconResolution = false,
                    UseSeparateLockedIconsWhenAvailable = false,
                    SeparateLockedIconEnabledGameIds = new HashSet<Guid> { gameId }
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(diskImageService, settings, logger: null);
                var unlockedTarget = diskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Unlocked);
                var lockedTarget = diskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    preserveOriginalResolution: false,
                    stem,
                    AchievementIconVariant.Locked);
                WritePlaceholderFile(unlockedTarget);
                WritePlaceholderFile(lockedTarget);

                var achievement = new AchievementDetail
                {
                    ApiName = apiName,
                    UnlockedIconPath = unlockedSource,
                    LockedIconPath = lockedSource
                };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { achievement }
                };

                await iconService.PopulateAchievementIconCacheAsync(data, CancellationToken.None);

                Assert.AreEqual(unlockedTarget, achievement.UnlockedIconPath);
                Assert.AreEqual(lockedTarget, achievement.LockedIconPath);
                Assert.AreNotEqual(achievement.UnlockedIconPath, achievement.LockedIconPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ShouldUseSeparateLockedIcons_UsesGlobalSettingOrPerGameOverride()
        {
            var gameId = Guid.NewGuid();
            var settings = new PersistedSettings
            {
                UseSeparateLockedIconsWhenAvailable = false
            };

            Assert.IsFalse(settings.ShouldUseSeparateLockedIcons(gameId));

            settings.SeparateLockedIconEnabledGameIds = new HashSet<Guid> { gameId };
            Assert.IsTrue(settings.ShouldUseSeparateLockedIcons(gameId));

            settings.SeparateLockedIconEnabledGameIds = new HashSet<Guid>();
            settings.UseSeparateLockedIconsWhenAvailable = true;
            Assert.IsTrue(settings.ShouldUseSeparateLockedIcons(gameId));
        }

        [TestMethod]
        public void ClearIconCache_CompressedOnly_RemovesCompressedFilesAndKeepsOriginalFiles()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var compressedUnlocked = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: false, "boss", AchievementIconVariant.Unlocked);
                var compressedLocked = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: false, "boss", AchievementIconVariant.Locked);
                var originalUnlocked = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: true, "boss", AchievementIconVariant.Unlocked);
                var legacyCompressed = diskImageService.GetIconCachePathFromUri("https://cdn.example.com/legacy-compressed.png", 128, gameId);
                var legacyOriginal = diskImageService.GetIconCachePathFromUri("https://cdn.example.com/legacy-original.png", 0, gameId);

                WritePlaceholderFile(compressedUnlocked);
                WritePlaceholderFile(compressedLocked);
                WritePlaceholderFile(originalUnlocked);
                WritePlaceholderFile(legacyCompressed);
                WritePlaceholderFile(legacyOriginal);

                var deletedCount = diskImageService.ClearIconCache(IconCacheClearScope.CompressedOnly);

                Assert.AreEqual(3, deletedCount);
                Assert.IsFalse(File.Exists(compressedUnlocked));
                Assert.IsFalse(File.Exists(compressedLocked));
                Assert.IsFalse(File.Exists(legacyCompressed));
                Assert.IsTrue(File.Exists(originalUnlocked));
                Assert.IsTrue(File.Exists(legacyOriginal));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ClearIconCache_FullResolutionOnly_RemovesOriginalFilesAndKeepsCompressedFiles()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var compressedUnlocked = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: false, "boss", AchievementIconVariant.Unlocked);
                var originalUnlocked = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: true, "boss", AchievementIconVariant.Unlocked);
                var originalLocked = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: true, "boss", AchievementIconVariant.Locked);
                var legacyCompressed = diskImageService.GetIconCachePathFromUri("https://cdn.example.com/legacy-compressed.png", 128, gameId);
                var legacyOriginal = diskImageService.GetIconCachePathFromUri("https://cdn.example.com/legacy-original.png", 0, gameId);

                WritePlaceholderFile(compressedUnlocked);
                WritePlaceholderFile(originalUnlocked);
                WritePlaceholderFile(originalLocked);
                WritePlaceholderFile(legacyCompressed);
                WritePlaceholderFile(legacyOriginal);

                var deletedCount = diskImageService.ClearIconCache(IconCacheClearScope.FullResolutionOnly);

                Assert.AreEqual(3, deletedCount);
                Assert.IsFalse(File.Exists(originalUnlocked));
                Assert.IsFalse(File.Exists(originalLocked));
                Assert.IsFalse(File.Exists(legacyOriginal));
                Assert.IsTrue(File.Exists(compressedUnlocked));
                Assert.IsTrue(File.Exists(legacyCompressed));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ClearIconCache_LockedOnly_RemovesNamedLockedFilesAndExplicitLegacyLockedPaths()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var unlockedPath = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: true, "boss", AchievementIconVariant.Unlocked);
                var lockedPath = diskImageService.GetAchievementIconCachePath(gameId, preserveOriginalResolution: true, "boss", AchievementIconVariant.Locked);
                var legacyLocked = diskImageService.GetIconCachePathFromUri("https://cdn.example.com/legacy-locked.png", 0, gameId);
                var legacyUnlocked = diskImageService.GetIconCachePathFromUri("https://cdn.example.com/legacy-unlocked.png", 0, gameId);

                WritePlaceholderFile(unlockedPath);
                WritePlaceholderFile(lockedPath);
                WritePlaceholderFile(legacyLocked);
                WritePlaceholderFile(legacyUnlocked);

                var deletedCount = diskImageService.ClearIconCache(
                    IconCacheClearScope.LockedOnly,
                    new[] { legacyLocked });

                Assert.AreEqual(2, deletedCount);
                Assert.IsTrue(File.Exists(unlockedPath));
                Assert.IsFalse(File.Exists(lockedPath));
                Assert.IsFalse(File.Exists(legacyLocked));
                Assert.IsTrue(File.Exists(legacyUnlocked));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void PersistedSettingsCloneAndCopy_PreserveAchievementIconCacheFlags()
        {
            var gameId = Guid.NewGuid();
            var source = new PersistedSettings
            {
                PreserveAchievementIconResolution = true,
                UseSeparateLockedIconsWhenAvailable = true,
                SeparateLockedIconEnabledGameIds = new HashSet<Guid> { gameId }
            };

            var clone = source.Clone();
            var copyTarget = new PersistedSettings();
            copyTarget.CopyFrom(source);

            Assert.IsTrue(clone.PreserveAchievementIconResolution);
            Assert.IsTrue(clone.UseSeparateLockedIconsWhenAvailable);
            Assert.IsTrue(clone.SeparateLockedIconEnabledGameIds.Contains(gameId));
            Assert.IsTrue(copyTarget.PreserveAchievementIconResolution);
            Assert.IsTrue(copyTarget.UseSeparateLockedIconsWhenAvailable);
            Assert.IsTrue(copyTarget.SeparateLockedIconEnabledGameIds.Contains(gameId));
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void WritePlaceholderFile(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        }
    }
}
