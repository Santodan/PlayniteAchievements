using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
                stem,
                AchievementIconVariant.Unlocked);
            var lockedPath = AchievementIconCachePathBuilder.BuildRelativePath(
                "game-123",
                stem,
                AchievementIconVariant.Locked);

            Assert.AreEqual(Path.Combine("icon_cache", "game-123", "original", "boss_win.png"), unlockedPath);
            Assert.AreEqual(Path.Combine("icon_cache", "game-123", "original", "boss_win.locked.png"), lockedPath);
        }

        [TestMethod]
        public void BuildDefaultCategoryRelativePath_UsesDefaultsFolderAndSingleArtFile()
        {
            var artPath = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                "game-123", "Phantom Liberty");

            StringAssert.StartsWith(artPath, Path.Combine("icon_cache", "game-123", "category_defaults", "category_Phantom Liberty_"));
            StringAssert.EndsWith(artPath, ".jpg");
            Assert.IsFalse(artPath.Contains(".icon."));
            Assert.IsFalse(artPath.Contains(".cover."));
        }

        [TestMethod]
        public void BuildDefaultCategoryRelativePath_IsDeterministicAndCaseStable()
        {
            var first = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                "game-123", "Expansion");
            var second = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                "game-123", "Expansion");
            var upper = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                "game-123", "EXPANSION");

            Assert.AreEqual(first, second);
            // The hash suffix is case-insensitive so write/read label casing differences
            // still land on the same file (paths differ only by the sanitized stem casing).
            StringAssert.EndsWith(upper, first.Substring(first.LastIndexOf('_')));
        }

        [TestMethod]
        public void BuildDefaultCategoryRelativePath_SanitizesLabelsOutsideCustomFolder()
        {
            var path = AchievementIconCachePathBuilder.BuildDefaultCategoryRelativePath(
                "game-123", "Blood/On:Crystal");

            StringAssert.Contains(path, Path.Combine("game-123", "category_defaults") + Path.DirectorySeparatorChar);
            StringAssert.Contains(path, "category_Blood_On_Crystal_");
            Assert.IsFalse(path.Contains(Path.Combine("game-123", "custom")));
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

                StringAssert.StartsWith(displayPath, "cachebust|");
                StringAssert.EndsWith(displayPath, "|" + lockedPath);
                Assert.IsTrue(AchievementIconResolver.HasExplicitLockedIcon(lockedPath, unlockedPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void GetUnlockedDisplayIcon_UsesCacheBustForLocalFiles()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var unlockedPath = Path.Combine(tempDir, "unlocked.png");
                WritePlaceholderFile(unlockedPath);

                var displayPath = AchievementIconResolver.GetUnlockedDisplayIcon(unlockedPath);

                StringAssert.StartsWith(displayPath, "cachebust|");
                StringAssert.EndsWith(displayPath, "|" + unlockedPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void GetLockedDisplayIcon_UsesCacheBustForGrayLocalFallback()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var unlockedPath = Path.Combine(tempDir, "unlocked.png");
                WritePlaceholderFile(unlockedPath);

                var displayPath = AchievementIconResolver.GetLockedDisplayIcon(unlockedPath, null);

                StringAssert.StartsWith(displayPath, "cachebust|");
                StringAssert.EndsWith(displayPath, "|gray:" + unlockedPath);
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
        public void GetLegacyCompatibleIcon_StripsCacheBustAndGrayPrefixes()
        {
            var iconPath = @"C:\icons\legacy.png";
            var wrapped = $"cachebust|123:456|gray:{iconPath}";

            var displayPath = AchievementIconResolver.GetLegacyCompatibleIcon(wrapped);

            Assert.AreEqual(iconPath, displayPath);
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
                    UseSeparateLockedIconsWhenAvailable = true
                };
                var enabledDiskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var enabledService = new AchievementIconService(
                    enabledDiskImageService,
                    new ManagedCustomIconService(enabledDiskImageService, logger: null),
                    enabledSettings,
                    logger: null);
                var unlockedTarget = enabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    stem,
                    AchievementIconVariant.Unlocked);
                var lockedTarget = enabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
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
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var disabledDiskImageService = new DiskImageService(logger: null, cacheRoot: Path.Combine(tempDir, "disabled"));
                var disabledService = new AchievementIconService(
                    disabledDiskImageService,
                    new ManagedCustomIconService(disabledDiskImageService, logger: null),
                    disabledSettings,
                    logger: null);
                var disabledUnlockedTarget = disabledDiskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
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
                    UseSeparateLockedIconsWhenAvailable = false,
                    SeparateLockedIconEnabledGameIds = new HashSet<Guid> { gameId }
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(
                    diskImageService,
                    new ManagedCustomIconService(diskImageService, logger: null),
                    settings,
                    logger: null);
                var unlockedTarget = diskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    stem,
                    AchievementIconVariant.Unlocked);
                var lockedTarget = diskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
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
        public async Task PopulateAchievementIconCacheAsync_ExplicitLockedOverrideMaterializesWhenSeparateLockedIconsDisabled()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var apiName = "custom_locked";
                var settings = new PersistedSettings
                {
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var iconService = new AchievementIconService(
                    diskImageService,
                    managedCustomIconService,
                    settings,
                    logger: null);

                var unlockedSource = Path.Combine(tempDir, "override-unlocked.png");
                var lockedSource = Path.Combine(tempDir, "override-locked.png");
                WriteSolidColorPng(unlockedSource, Colors.Red);
                WriteSolidColorPng(lockedSource, Colors.Blue);

                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [apiName] = unlockedSource
                };
                var lockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [apiName] = lockedSource
                };

                var achievement = new AchievementDetail
                {
                    ApiName = apiName,
                    UnlockedIconPath = "https://cdn.example.com/provider-unlocked.png",
                    LockedIconPath = "https://cdn.example.com/provider-locked.png"
                };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { achievement }
                };

                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    unlockedOverrides,
                    lockedOverrides,
                    CancellationToken.None);

                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];
                var unlockedTarget = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    stem,
                    AchievementIconVariant.Unlocked);
                var lockedTarget = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    stem,
                    AchievementIconVariant.Locked);

                Assert.AreEqual(unlockedTarget, achievement.UnlockedIconPath);
                Assert.AreEqual(lockedTarget, achievement.LockedIconPath);
                Assert.IsTrue(File.Exists(unlockedTarget));
                Assert.IsTrue(File.Exists(lockedTarget));
                Assert.AreNotEqual(achievement.UnlockedIconPath, achievement.LockedIconPath);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_ExplicitUnlockedOverrideSuppressesProviderLockedDownload()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var apiName = "custom_unlocked_only";
                var settings = new PersistedSettings
                {
                    UseSeparateLockedIconsWhenAvailable = true
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var iconService = new AchievementIconService(
                    diskImageService,
                    managedCustomIconService,
                    settings,
                    logger: null);
                var unlockedSource = Path.Combine(tempDir, "override-unlocked.png");
                WriteSolidColorPng(unlockedSource, Colors.Red);

                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [apiName] = unlockedSource
                };

                var achievement = new AchievementDetail
                {
                    ApiName = apiName,
                    UnlockedIconPath = "https://cdn.example.com/provider-unlocked.png",
                    LockedIconPath = "https://cdn.example.com/provider-locked.png"
                };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { achievement }
                };

                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    unlockedOverrides,
                    null,
                    CancellationToken.None);

                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];
                var unlockedTarget = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId.ToString("D"),
                    stem,
                    AchievementIconVariant.Unlocked);
                var lockedTarget = diskImageService.GetAchievementIconCachePath(
                    gameId.ToString("D"),
                    stem,
                    AchievementIconVariant.Locked);

                Assert.AreEqual(unlockedTarget, achievement.UnlockedIconPath);
                Assert.AreEqual(unlockedTarget, achievement.LockedIconPath);
                Assert.IsTrue(File.Exists(unlockedTarget));
                Assert.IsFalse(File.Exists(lockedTarget));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_ForceRefreshExistingTargets_ReplacesCachedFile()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var settings = new PersistedSettings
                {
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(
                    diskImageService,
                    new ManagedCustomIconService(diskImageService, logger: null),
                    settings,
                    logger: null);
                var sourceOne = Path.Combine(tempDir, "source-one.png");
                var sourceTwo = Path.Combine(tempDir, "source-two.png");
                WriteSolidColorPng(sourceOne, Colors.Red);
                WriteSolidColorPng(sourceTwo, Colors.Blue);

                var achievement = new AchievementDetail
                {
                    ApiName = "force_refresh_icon",
                    UnlockedIconPath = sourceOne,
                    LockedIconPath = sourceOne
                };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { achievement }
                };

                await iconService.PopulateAchievementIconCacheAsync(data, CancellationToken.None);

                var targetPath = achievement.UnlockedIconPath;
                var firstBytes = File.ReadAllBytes(targetPath);

                achievement.UnlockedIconPath = sourceTwo;
                achievement.LockedIconPath = sourceTwo;
                await iconService.PopulateAchievementIconCacheAsync(data, CancellationToken.None);

                Assert.IsTrue(firstBytes.SequenceEqual(File.ReadAllBytes(targetPath)));

                achievement.UnlockedIconPath = sourceTwo;
                achievement.LockedIconPath = sourceTwo;
                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    CancellationToken.None,
                    forceRefreshExistingTargets: true);

                Assert.IsFalse(firstBytes.SequenceEqual(File.ReadAllBytes(targetPath)));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_ForceOverrideApiNames_RematerializesOnlyListedAchievements()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var settings = new PersistedSettings
                {
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var iconService = new AchievementIconService(
                    diskImageService,
                    managedCustomIconService,
                    settings,
                    logger: null);

                var changedSource = Path.Combine(tempDir, "override-changed.png");
                var unchangedSource = Path.Combine(tempDir, "override-unchanged.png");
                WriteSolidColorPng(changedSource, Colors.Red);
                WriteSolidColorPng(unchangedSource, Colors.Red);

                var changedAchievement = new AchievementDetail { ApiName = "changed_override" };
                var unchangedAchievement = new AchievementDetail { ApiName = "unchanged_override" };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { changedAchievement, unchangedAchievement }
                };
                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [changedAchievement.ApiName] = changedSource,
                    [unchangedAchievement.ApiName] = unchangedSource
                };

                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    unlockedOverrides,
                    null,
                    CancellationToken.None);

                var changedTarget = changedAchievement.UnlockedIconPath;
                var unchangedTarget = unchangedAchievement.UnlockedIconPath;
                var changedFirstBytes = File.ReadAllBytes(changedTarget);
                var unchangedFirstBytes = File.ReadAllBytes(unchangedTarget);

                WriteSolidColorPng(changedSource, Colors.Blue);
                WriteSolidColorPng(unchangedSource, Colors.Blue);

                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    unlockedOverrides,
                    null,
                    CancellationToken.None,
                    forceOverrideApiNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        changedAchievement.ApiName
                    });

                Assert.IsFalse(changedFirstBytes.SequenceEqual(File.ReadAllBytes(changedTarget)));
                Assert.IsTrue(unchangedFirstBytes.SequenceEqual(File.ReadAllBytes(unchangedTarget)));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateAchievementIconCacheAsync_ClearedOverrideRestoresDefaultCachedIcon()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid();
                var settings = new PersistedSettings
                {
                    UseSeparateLockedIconsWhenAvailable = false
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var iconService = new AchievementIconService(
                    diskImageService,
                    managedCustomIconService,
                    settings,
                    logger: null);

                var providerSource = Path.Combine(tempDir, "provider.png");
                var overrideSource = Path.Combine(tempDir, "override.png");
                WriteSolidColorPng(providerSource, Colors.Red);
                WriteSolidColorPng(overrideSource, Colors.Blue);

                var achievement = new AchievementDetail
                {
                    ApiName = "cleared_override",
                    UnlockedIconPath = providerSource,
                    LockedIconPath = providerSource
                };
                var data = new GameAchievementData
                {
                    PlayniteGameId = gameId,
                    Achievements = { achievement }
                };

                await iconService.PopulateAchievementIconCacheAsync(data, CancellationToken.None);
                var defaultTarget = achievement.UnlockedIconPath;
                Assert.IsTrue(File.Exists(defaultTarget));

                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [achievement.ApiName] = overrideSource
                };
                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    unlockedOverrides,
                    null,
                    CancellationToken.None);
                Assert.AreNotEqual(defaultTarget, achievement.UnlockedIconPath);

                await iconService.PopulateAchievementIconCacheAsync(
                    data,
                    null,
                    null,
                    CancellationToken.None,
                    forceOverrideApiNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        achievement.ApiName
                    });

                Assert.AreEqual(defaultTarget, achievement.UnlockedIconPath);
                Assert.IsTrue(File.Exists(achievement.UnlockedIconPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task DiskImageService_ReleasesPathLocksAfterUniqueLocalCopies()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var sourcePath = Path.Combine(tempDir, "source.png");
                WriteSolidColorPng(sourcePath, Colors.Red);
                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    for (var i = 0; i < 50; i++)
                    {
                        var targetPath = Path.Combine(tempDir, "targets", $"icon-{i}.png");
                        var result = await diskImageService.GetOrCopyLocalIconToPathAsync(
                            sourcePath,
                            targetPath,
                            128,
                            CancellationToken.None);

                        Assert.AreEqual(targetPath, result);
                        Assert.IsTrue(File.Exists(targetPath));
                    }

                    Assert.AreEqual(0, diskImageService.PathWriteLockCountForTests);
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task DiskImageService_ReleasesPathLockAfterConcurrentSameTargetCopies()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var sourcePath = Path.Combine(tempDir, "source.png");
                var targetPath = Path.Combine(tempDir, "target.png");
                WriteSolidColorPng(sourcePath, Colors.Blue);
                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                {
                    var tasks = Enumerable.Range(0, 32)
                        .Select(_ => diskImageService.GetOrCopyLocalIconToPathAsync(
                            sourcePath,
                            targetPath,
                            128,
                            CancellationToken.None))
                        .ToArray();

                    var results = await Task.WhenAll(tasks);

                    Assert.IsTrue(results.All(result => string.Equals(result, targetPath, StringComparison.OrdinalIgnoreCase)));
                    Assert.IsTrue(File.Exists(targetPath));
                    Assert.AreEqual(0, diskImageService.PathWriteLockCountForTests);
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task MemoryImageService_EvictByUriSegment_RemovesOnlyMatchingEntries()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameAIcon = Path.Combine(tempDir, "icon_cache", "game-a", "128", "boss.png");
                var gameBIcon = Path.Combine(tempDir, "icon_cache", "game-b", "128", "boss.png");
                WriteSolidColorPng(gameAIcon, Colors.Red);
                WriteSolidColorPng(gameBIcon, Colors.Blue);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                using (var memoryImageService = new MemoryImageService(logger: null, diskImageService))
                {
                    Assert.IsNotNull(await memoryImageService.GetAsync(gameAIcon, 64, CancellationToken.None));
                    Assert.IsNotNull(await memoryImageService.GetAsync(gameBIcon, 64, CancellationToken.None));

                    // Both keep serving from the memory cache after the files are gone.
                    File.Delete(gameAIcon);
                    File.Delete(gameBIcon);
                    Assert.IsNotNull(await memoryImageService.GetAsync(gameAIcon, 64, CancellationToken.None));
                    Assert.IsNotNull(await memoryImageService.GetAsync(gameBIcon, 64, CancellationToken.None));

                    memoryImageService.EvictByUriSegment("game-a");

                    Assert.IsNull(await memoryImageService.GetAsync(gameAIcon, 64, CancellationToken.None));
                    Assert.IsNotNull(await memoryImageService.GetAsync(gameBIcon, 64, CancellationToken.None));
                }
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task MemoryImageService_InPlaceOverwriteEvictsCachedBitmap()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var sourceRed = Path.Combine(tempDir, "source-red.png");
                var sourceBlue = Path.Combine(tempDir, "source-blue.png");
                var targetPath = Path.Combine(tempDir, "icon_cache", "game-a", "128", "boss.png");
                WriteSolidColorPng(sourceRed, Colors.Red);
                WriteSolidColorPng(sourceBlue, Colors.Blue);

                using (var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir))
                using (var memoryImageService = new MemoryImageService(logger: null, diskImageService))
                {
                    string overwrittenPath = null;
                    diskImageService.ImageFileOverwritten += path => overwrittenPath = path;

                    Assert.AreEqual(targetPath, await diskImageService.GetOrCopyLocalIconToPathAsync(
                        sourceRed, targetPath, 128, CancellationToken.None));
                    Assert.IsNotNull(await memoryImageService.GetAsync(targetPath, 64, CancellationToken.None));

                    // A copy without overwrite leaves the existing file alone and raises nothing.
                    Assert.AreEqual(targetPath, await diskImageService.GetOrCopyLocalIconToPathAsync(
                        sourceBlue, targetPath, 128, CancellationToken.None));
                    Assert.IsNull(overwrittenPath);

                    Assert.AreEqual(targetPath, await diskImageService.GetOrCopyLocalIconToPathAsync(
                        sourceBlue, targetPath, 128, CancellationToken.None, overwriteExistingTarget: true));
                    Assert.AreEqual(targetPath, overwrittenPath);

                    // The overwrite evicted the cached bitmap: with the file gone, the next
                    // request cannot be served from memory anymore.
                    File.Delete(targetPath);
                    Assert.IsNull(await memoryImageService.GetAsync(targetPath, 64, CancellationToken.None));
                }
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
        public void ClearIconCache_LockedOnly_RemovesNamedLockedFilesAndExplicitLegacyLockedPaths()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var unlockedPath = diskImageService.GetAchievementIconCachePath(gameId, "boss", AchievementIconVariant.Unlocked);
                var lockedPath = diskImageService.GetAchievementIconCachePath(gameId, "boss", AchievementIconVariant.Locked);
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
        public void ClearIconCache_ReportsDeletionProgress()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var firstPath = diskImageService.GetAchievementIconCachePath(gameId, "boss_one", AchievementIconVariant.Unlocked);
                var secondPath = diskImageService.GetAchievementIconCachePath(gameId, "boss_two", AchievementIconVariant.Unlocked);
                var snapshots = new List<Tuple<int, int>>();

                WritePlaceholderFile(firstPath);
                WritePlaceholderFile(secondPath);

                var deletedCount = diskImageService.ClearIconCache(
                    IconCacheClearScope.All,
                    reportDeleteProgress: (processed, total) => snapshots.Add(Tuple.Create(processed, total)));

                Assert.AreEqual(2, deletedCount);
                Assert.IsTrue(snapshots.Count >= 2);
                Assert.AreEqual(0, snapshots[0].Item1);
                Assert.AreEqual(2, snapshots[0].Item2);
                var last = snapshots[snapshots.Count - 1];
                Assert.AreEqual(2, last.Item1);
                Assert.AreEqual(2, last.Item2);
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void ClearGameCache_PreservesManagedCustomFiles()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var managedCustomIconService = new ManagedCustomIconService(diskImageService, logger: null);
                var cachedPath = diskImageService.GetAchievementIconCachePath(
                    gameId,
                    "boss",
                    AchievementIconVariant.Unlocked);
                var customPath = managedCustomIconService.GetAchievementCustomIconPath(
                    gameId,
                    "boss",
                    AchievementIconVariant.Unlocked);

                WritePlaceholderFile(cachedPath);
                WritePlaceholderFile(customPath);

                diskImageService.ClearGameCache(gameId);

                Assert.IsFalse(File.Exists(cachedPath));
                Assert.IsTrue(File.Exists(customPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void DeleteLegacyCompressedGameIconFolder_RemovesOnlyLegacyFolderAndIsIdempotent()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var gameId = Guid.NewGuid().ToString("D");
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var legacyPath = diskImageService.GetLegacyCompressedAchievementIconCachePath(
                    gameId, "boss", AchievementIconVariant.Unlocked);
                var originalPath = diskImageService.GetAchievementIconCachePath(
                    gameId, "boss", AchievementIconVariant.Unlocked);

                WritePlaceholderFile(legacyPath);
                WritePlaceholderFile(originalPath);

                diskImageService.DeleteLegacyCompressedGameIconFolder(gameId);

                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(legacyPath)));
                Assert.IsTrue(File.Exists(originalPath));

                // Second call is a no-op once the folder is gone.
                diskImageService.DeleteLegacyCompressedGameIconFolder(gameId);

                Assert.IsTrue(File.Exists(originalPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateFriendAchievementIconCacheAsync_NeverDownloadsSeparateLockedIcon()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                // Separate locked icons are enabled globally to prove friends still ignore them.
                var settings = new PersistedSettings
                {
                    UseSeparateLockedIconsWhenAvailable = true
                };
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(
                    diskImageService,
                    new ManagedCustomIconService(diskImageService, logger: null),
                    settings,
                    logger: null);

                const string providerKey = "Steam";
                const string providerGameKey = "440";
                const string apiName = "friend_ach";
                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { apiName })[apiName];

                var unlockedTarget = diskImageService.ResolveCacheRelativePath(
                    FriendImageCachePathBuilder.BuildGameImageRelativePath(
                        providerKey,
                        providerGameKey,
                        FriendImageCachePathBuilder.GetAchievementFileName(stem, AchievementIconVariant.Unlocked)));
                var lockedTarget = diskImageService.ResolveCacheRelativePath(
                    FriendImageCachePathBuilder.BuildGameImageRelativePath(
                        providerKey,
                        providerGameKey,
                        FriendImageCachePathBuilder.GetAchievementFileName(stem, AchievementIconVariant.Locked)));

                // Pre-seed only the unlocked target so no network download is required.
                WritePlaceholderFile(unlockedTarget);

                var achievement = new AchievementDetail
                {
                    ApiName = apiName,
                    UnlockedIconPath = "https://cdn.example.com/icons/unlocked.png",
                    LockedIconPath = "https://cdn.example.com/icons/locked.png"
                };
                var definition = new FriendGameDefinition
                {
                    ProviderKey = providerKey,
                    ProviderGameKey = providerGameKey,
                    Achievements = { achievement }
                };

                await iconService.PopulateFriendAchievementIconCacheAsync(definition, CancellationToken.None);

                Assert.AreEqual(unlockedTarget, achievement.UnlockedIconPath);
                // Locked mirrors unlocked; the distinct locked source is never downloaded.
                Assert.AreEqual(achievement.UnlockedIconPath, achievement.LockedIconPath);
                Assert.IsFalse(File.Exists(lockedTarget));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateFriendGameImageCacheAsync_CachesDistinctIconAndCoverPaths()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var sourceRoot = Path.Combine(tempDir, "sources");
                Directory.CreateDirectory(sourceRoot);
                var iconSource = Path.Combine(sourceRoot, "icon.jpg");
                var coverSource = Path.Combine(sourceRoot, "cover.jpg");
                WritePlaceholderFile(iconSource);
                WritePlaceholderFile(coverSource);

                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(
                    diskImageService,
                    new ManagedCustomIconService(diskImageService, logger: null),
                    new PersistedSettings(),
                    logger: null);

                var result = await iconService.PopulateFriendGameImageCacheAsync(
                        "Steam",
                        "100",
                        iconSource,
                        coverSource,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var expectedIconPath = Path.ChangeExtension(
                    diskImageService.ResolveCacheRelativePath(
                        FriendImageCachePathBuilder.BuildGameImageRelativePath(
                            "Steam",
                            "100",
                            FriendImageCachePathBuilder.GameIconFileName)),
                    ".jpg");
                var expectedCoverPath = Path.ChangeExtension(
                    diskImageService.ResolveCacheRelativePath(
                        FriendImageCachePathBuilder.BuildGameImageRelativePath(
                            "Steam",
                            "100",
                            FriendImageCachePathBuilder.GameCoverFileName)),
                    ".jpg");

                Assert.AreEqual(expectedIconPath, result.IconPath);
                Assert.AreEqual(expectedCoverPath, result.CoverPath);
                Assert.IsTrue(File.Exists(expectedIconPath));
                Assert.IsTrue(File.Exists(expectedCoverPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task PopulateFriendGameImageCacheAsync_PrefersResolvedOriginalFormatOverLegacyPngTarget()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var sourceRoot = Path.Combine(tempDir, "sources");
                Directory.CreateDirectory(sourceRoot);
                var iconSource = Path.Combine(sourceRoot, "icon.jpg");
                WritePlaceholderFile(iconSource);

                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var legacyPngTarget = diskImageService.ResolveCacheRelativePath(
                    FriendImageCachePathBuilder.BuildGameImageRelativePath(
                        "Steam",
                        "100",
                        FriendImageCachePathBuilder.GameIconFileName));
                WritePlaceholderFile(legacyPngTarget);

                var iconService = new AchievementIconService(
                    diskImageService,
                    new ManagedCustomIconService(diskImageService, logger: null),
                    new PersistedSettings(),
                    logger: null);

                var result = await iconService.PopulateFriendGameImageCacheAsync(
                        "Steam",
                        "100",
                        iconSource,
                        coverSourcePath: null,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var expectedIconPath = Path.ChangeExtension(legacyPngTarget, ".jpg");
                Assert.AreEqual(expectedIconPath, result.IconPath);
                Assert.IsTrue(File.Exists(expectedIconPath));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void DeleteFriendGameIconCache_RemovesOnlyTargetGameFolder()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var iconService = new AchievementIconService(
                    diskImageService,
                    new ManagedCustomIconService(diskImageService, logger: null),
                    new PersistedSettings(),
                    logger: null);

                var stem = AchievementIconCachePathBuilder.BuildFileStems(new[] { "ach" })["ach"];

                string FriendIconPath(string provider, string gameKey) =>
                    diskImageService.ResolveCacheRelativePath(
                        FriendImageCachePathBuilder.BuildGameImageRelativePath(
                            provider,
                            gameKey,
                            FriendImageCachePathBuilder.GetAchievementFileName(stem, AchievementIconVariant.Unlocked)));

                var targetGameIcon = FriendIconPath("Steam", "440");
                var otherGameIcon = FriendIconPath("Steam", "570");
                var ownedIcon = diskImageService.GetAchievementIconCachePath(
                    Guid.NewGuid().ToString("D"),
                    stem,
                    AchievementIconVariant.Unlocked);

                WritePlaceholderFile(targetGameIcon);
                WritePlaceholderFile(otherGameIcon);
                WritePlaceholderFile(ownedIcon);

                iconService.DeleteFriendGameIconCache("Steam", "440");

                Assert.IsFalse(File.Exists(targetGameIcon), "Target friend game icons should be deleted.");
                Assert.IsFalse(
                    Directory.Exists(Path.GetDirectoryName(targetGameIcon)),
                    "Target friend game folder should be removed.");
                Assert.IsTrue(File.Exists(otherGameIcon), "Other friend game icons must be untouched.");
                Assert.IsTrue(File.Exists(ownedIcon), "Owned game icons must be untouched.");
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public void PersistedSettingsCloneAndCopy_PreservesSeparateLockedIconFlags()
        {
            var gameId = Guid.NewGuid();
            var source = new PersistedSettings
            {
                UseSeparateLockedIconsWhenAvailable = true,
                SeparateLockedIconEnabledGameIds = new HashSet<Guid> { gameId }
            };

            var clone = source.Clone();
            var copyTarget = new PersistedSettings();
            copyTarget.CopyFrom(source);

            Assert.IsTrue(clone.UseSeparateLockedIconsWhenAvailable);
            Assert.IsTrue(clone.SeparateLockedIconEnabledGameIds.Contains(gameId));
            Assert.IsTrue(copyTarget.UseSeparateLockedIconsWhenAvailable);
            Assert.IsTrue(copyTarget.SeparateLockedIconEnabledGameIds.Contains(gameId));
        }

        [TestMethod]
        public void FindExistingDefaultCategoryImagePath_ProbesSourceFormatExtensions()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var gameId = Guid.NewGuid().ToString("D");

                Assert.IsNull(diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));

                // decodeSize 0 downloads keep the source extension, so a .png source lands
                // beside the canonical .jpg path with its extension swapped. Direct disk writes
                // bypass the service's write seams, so the snapshot must be invalidated manually.
                var canonicalPath = diskImageService.GetDefaultCategoryImagePath(
                    gameId, "Bonus");
                var pngPath = Path.ChangeExtension(canonicalPath, ".png");
                WritePlaceholderFile(pngPath);
                diskImageService.InvalidateDefaultCategoryArtSnapshot(gameId);

                Assert.AreEqual(pngPath, diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));

                // The canonical .jpg wins when both exist.
                WritePlaceholderFile(canonicalPath);
                diskImageService.InvalidateDefaultCategoryArtSnapshot(gameId);
                Assert.AreEqual(canonicalPath, diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task FindExistingDefaultCategoryImagePath_SeesWritesThroughServiceWithoutManualInvalidation()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var gameId = Guid.NewGuid().ToString("D");

                // Caches the empty snapshot before the write.
                Assert.IsNull(diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));

                var sourcePath = Path.Combine(tempDir, "source.png");
                WritePlaceholderFile(sourcePath);
                var written = await diskImageService.GetOrCopyLocalIconToPathAsync(
                    sourcePath,
                    diskImageService.GetDefaultCategoryImagePath(gameId, "Bonus"),
                    decodeSize: 0,
                    CancellationToken.None);

                Assert.IsNotNull(written);
                Assert.AreEqual(written, diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task FindExistingDefaultCategoryImagePath_ClearGameCacheInvalidatesSnapshot()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var gameId = Guid.NewGuid().ToString("D");

                var sourcePath = Path.Combine(tempDir, "source.png");
                WritePlaceholderFile(sourcePath);
                var written = await diskImageService.GetOrCopyLocalIconToPathAsync(
                    sourcePath,
                    diskImageService.GetDefaultCategoryImagePath(gameId, "Bonus"),
                    decodeSize: 0,
                    CancellationToken.None);

                Assert.AreEqual(written, diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));

                diskImageService.ClearGameCache(gameId);

                Assert.IsNull(diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        [TestMethod]
        public async Task FindExistingDefaultCategoryImagePath_ClearIconCacheInvalidatesSnapshot()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var diskImageService = new DiskImageService(logger: null, cacheRoot: tempDir);
                var gameId = Guid.NewGuid().ToString("D");

                var sourcePath = Path.Combine(tempDir, "source.png");
                WritePlaceholderFile(sourcePath);
                var written = await diskImageService.GetOrCopyLocalIconToPathAsync(
                    sourcePath,
                    diskImageService.GetDefaultCategoryImagePath(gameId, "Bonus"),
                    decodeSize: 0,
                    CancellationToken.None);

                Assert.AreEqual(written, diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));

                diskImageService.ClearIconCache(IconCacheClearScope.All);

                Assert.IsNull(diskImageService.FindExistingDefaultCategoryImagePath(
                    gameId, "Bonus"));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
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

        private static void WriteSolidColorPng(string path, Color color)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var pixels = new byte[]
            {
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A,
                color.B, color.G, color.R, color.A
            };
            var bitmap = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                8);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                encoder.Save(stream);
            }
        }

    }
}
