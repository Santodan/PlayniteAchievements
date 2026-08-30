using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Tests.Images
{
    [TestClass]
    public class CategoryArtChainResolverTests
    {
        private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private Func<string, Guid?, CategoryArtDisplayMode, string> _savedOverrideResolver;
        private Func<DiskImageService> _savedDiskImageAccessor;
        private string _tempDir;

        [TestInitialize]
        public void Initialize()
        {
            _savedOverrideResolver = CategoryArtChainResolver.OverrideDisplayPathResolver;
            _savedDiskImageAccessor = CategoryDefaultImageResolver.DiskImageServiceAccessor;
        }

        [TestCleanup]
        public void Cleanup()
        {
            CategoryArtChainResolver.OverrideDisplayPathResolver = _savedOverrideResolver;
            CategoryDefaultImageResolver.DiskImageServiceAccessor = _savedDiskImageAccessor;

            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
                catch
                {
                    // A leftover temp directory must not fail the test.
                }
            }
        }

        private static Dictionary<string, CategoryImageOverrideData> Overrides(
            params string[] labelAndArtPairs)
        {
            var result = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < labelAndArtPairs.Length; i += 2)
            {
                result[labelAndArtPairs[i]] = new CategoryImageOverrideData { Art = labelAndArtPairs[i + 1] };
            }

            return result;
        }

        private static string Resolve(
            string label,
            IReadOnlyDictionary<string, CategoryImageOverrideData> overrides,
            out IReadOnlyList<string> perLevel,
            CategoryArtChainMemo memo = null,
            string providerLabel = null,
            CategoryArtDisplayMode displayMode = CategoryArtDisplayMode.FilePath)
        {
            return CategoryArtChainResolver.Resolve(
                GameId,
                label,
                providerLabel,
                overrides,
                displayMode,
                memo,
                out perLevel);
        }

        /// <summary>
        /// Installs a real DiskImageService over a temp directory so provider default art can be
        /// created on disk, and returns it for building canonical default-art paths.
        /// </summary>
        private DiskImageService UseRealDefaultArtStore()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "pa-artchain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var diskImageService = new DiskImageService(logger: null, cacheRoot: _tempDir);
            CategoryDefaultImageResolver.DiskImageServiceAccessor = () => diskImageService;
            return diskImageService;
        }

        private static string DefaultArtPath(DiskImageService diskImageService, string categoryLabel)
        {
            return diskImageService.GetDefaultCategoryImagePath(GameId.ToString("D"), categoryLabel);
        }

        private static void WriteDefaultArt(DiskImageService diskImageService, string categoryLabel)
        {
            var path = DefaultArtPath(diskImageService, categoryLabel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        }

        // ---- override side (hermetic: no disk accessor installed, so default probes return null)

        [TestMethod]
        public void Resolve_UsesTheNodesOwnOverride()
        {
            Assert.AreEqual("winter.png", Resolve("DLC::Winter", Overrides("DLC::Winter", "winter.png"), out _));
        }

        [TestMethod]
        public void Resolve_InheritsFromTheNearestAncestorWhenTheNodeHasNone()
        {
            var overrides = Overrides("DLC", "dlc.png");

            Assert.AreEqual("dlc.png", Resolve("DLC::Winter", overrides, out _));
            Assert.AreEqual("dlc.png", Resolve("DLC::Winter::Week1", overrides, out _));
        }

        [TestMethod]
        public void Resolve_PrefersTheNearerAncestorAndTheNodeItselfOverBoth()
        {
            var overrides = Overrides("DLC", "dlc.png", "DLC::Winter", "winter.png");

            Assert.AreEqual("winter.png", Resolve("DLC::Winter::Week1", overrides, out _));
            Assert.AreEqual("winter.png", Resolve("DLC::Winter", overrides, out _));
            Assert.AreEqual("dlc.png", Resolve("DLC", overrides, out _));
        }

        [TestMethod]
        public void Resolve_ReturnsNullWhenNothingInTheChainHasArt()
        {
            Assert.IsNull(Resolve("DLC::Winter", Overrides("Other", "other.png"), out _));
        }

        [TestMethod]
        public void Resolve_IsUnchangedForAFlatLabel()
        {
            Assert.AreEqual("dlc.png", Resolve("DLC", Overrides("DLC", "dlc.png"), out var perLevel));
            Assert.AreEqual(1, perLevel.Count);
            Assert.IsNull(Resolve("DLC", Overrides("Other", "other.png"), out _));
        }

        [TestMethod]
        public void Resolve_ReportsArtPerLevelRootFirst()
        {
            var overrides = Overrides("DLC", "dlc.png", "DLC::Winter::Week1", "week1.png");

            Resolve("DLC::Winter::Week1", overrides, out var perLevel);

            // Index (depth - 1) is that level's own art: an aggregate row for "DLC" must find
            // dlc.png rather than the leaf's week1.png.
            CollectionAssert.AreEqual(new[] { "dlc.png", null, "week1.png" }, (string[])perLevel);
        }

        [TestMethod]
        public void Resolve_DoesNotLetADescendantsArtLeakUpIntoAnAncestorLevel()
        {
            Resolve("DLC::Winter", Overrides("DLC::Winter", "winter.png"), out var perLevel);

            Assert.IsNull(perLevel[0], "the parent level has no art of its own");
            Assert.AreEqual("winter.png", perLevel[1]);
        }

        [TestMethod]
        public void Resolve_TrimsAndDropsBlankStoredValues()
        {
            Assert.AreEqual("winter.png", Resolve("DLC::Winter", Overrides("DLC::Winter", "  winter.png  "), out _));
            Assert.IsNull(Resolve("DLC::Winter", Overrides("DLC::Winter", "   "), out _));
        }

        // ---- provider default art on disk

        [TestMethod]
        public void Resolve_PrefersTheEffectiveLabelsDefaultArtOverTheProviderLabels()
        {
            // A merge leaves an achievement carrying its original provider label while its
            // effective label points at the target category. Probing effective-first is what makes
            // a merged category show the target's art; with provider-first every surface using it
            // kept showing the pre-merge image.
            var diskImageService = UseRealDefaultArtStore();
            WriteDefaultArt(diskImageService, "Target");
            WriteDefaultArt(diskImageService, "Source");

            var art = Resolve("Target", overrides: null, out _, providerLabel: "Source");

            Assert.AreEqual(DefaultArtPath(diskImageService, "Target"), art);
        }

        [TestMethod]
        public void Resolve_FallsBackToTheProviderLabelWhenTheEffectiveLabelHasNoDefaultArt()
        {
            // A plain rename: the renamed label has no default file of its own, so the provider
            // label still supplies the art.
            var diskImageService = UseRealDefaultArtStore();
            WriteDefaultArt(diskImageService, "Source");

            var art = Resolve("Renamed", overrides: null, out _, providerLabel: "Source");

            Assert.AreEqual(DefaultArtPath(diskImageService, "Source"), art);
        }

        [TestMethod]
        public void Resolve_InheritsAnAncestorsDefaultArt()
        {
            var diskImageService = UseRealDefaultArtStore();
            WriteDefaultArt(diskImageService, "DLC");

            var art = Resolve("DLC::Winter::Week1", overrides: null, out var perLevel);

            Assert.AreEqual(DefaultArtPath(diskImageService, "DLC"), art);
            Assert.IsNull(perLevel[1], "an intermediate level with no art of its own stays null");
        }

        [TestMethod]
        public void Resolve_AppliesAnOverrideAheadOfDefaultArtAtTheSameLevel()
        {
            var diskImageService = UseRealDefaultArtStore();
            WriteDefaultArt(diskImageService, "DLC");

            Assert.AreEqual("custom.png", Resolve("DLC", Overrides("DLC", "custom.png"), out _));
        }

        [TestMethod]
        public void Resolve_TreatsTheProviderFallbackAsBelongingToTheDeepestLevelOnly()
        {
            // The provider label describes the achievement's own category, so it must not be
            // consulted while walking ancestors.
            var diskImageService = UseRealDefaultArtStore();
            WriteDefaultArt(diskImageService, "Source");

            Resolve("DLC::Winter", overrides: null, out var perLevel, providerLabel: "Source");

            Assert.IsNull(perLevel[0], "the ancestor level must not pick up the provider fallback");
            Assert.AreEqual(DefaultArtPath(diskImageService, "Source"), perLevel[1]);
        }

        // ---- display mode and memoization

        [TestMethod]
        public void Resolve_PassesTheDisplayModeToTheInstalledPathResolver()
        {
            CategoryArtChainResolver.OverrideDisplayPathResolver =
                (value, gameId, mode) => mode + ":" + value;

            Assert.AreEqual(
                "FilePath:stored.png",
                Resolve("DLC", Overrides("DLC", "stored.png"), out _));
            Assert.AreEqual(
                "PluginImagePipeline:stored.png",
                Resolve("DLC", Overrides("DLC", "stored.png"), out _,
                    displayMode: CategoryArtDisplayMode.PluginImagePipeline));
        }

        [TestMethod]
        public void Resolve_ResolvesAnAncestorOncePerPassWhenGivenAMemo()
        {
            var lookups = 0;
            CategoryArtChainResolver.OverrideDisplayPathResolver = (value, gameId, mode) =>
            {
                lookups++;
                return value;
            };

            var overrides = Overrides("DLC", "dlc.png");
            var memo = new CategoryArtChainMemo();
            Resolve("DLC::Winter", overrides, out _, memo);
            Resolve("DLC::Summer", overrides, out _, memo);
            Resolve("DLC::Autumn", overrides, out _, memo);

            Assert.AreEqual(1, lookups, "the shared 'DLC' ancestor must be resolved once for the pass");
        }

        [TestMethod]
        public void Resolve_ResolvesAnAncestorPerLeafWithoutAMemo()
        {
            var lookups = 0;
            CategoryArtChainResolver.OverrideDisplayPathResolver = (value, gameId, mode) =>
            {
                lookups++;
                return value;
            };

            var overrides = Overrides("DLC", "dlc.png");
            Resolve("DLC::Winter", overrides, out _);
            Resolve("DLC::Summer", overrides, out _);

            Assert.AreEqual(2, lookups, "this is the repeated work the memo exists to remove");
        }

        [TestMethod]
        public void Resolve_KeepsDisplayModesApartInTheMemo()
        {
            CategoryArtChainResolver.OverrideDisplayPathResolver =
                (value, gameId, mode) => mode + ":" + value;

            var overrides = Overrides("DLC", "dlc.png");
            var memo = new CategoryArtChainMemo();

            Assert.AreEqual("FilePath:dlc.png", Resolve("DLC", overrides, out _, memo));
            Assert.AreEqual(
                "PluginImagePipeline:dlc.png",
                Resolve("DLC", overrides, out _, memo, displayMode: CategoryArtDisplayMode.PluginImagePipeline));
        }

        [TestMethod]
        public void Resolve_UsesTheStoredValueWhenNoPathResolverIsInstalled()
        {
            CategoryArtChainResolver.OverrideDisplayPathResolver = null;

            Assert.AreEqual("stored.png", Resolve("DLC", Overrides("DLC", "stored.png"), out _));
        }
    }
}
